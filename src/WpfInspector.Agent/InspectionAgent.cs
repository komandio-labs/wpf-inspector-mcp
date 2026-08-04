using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace WpfInspector.Agent;

internal sealed class InspectionAgent(Dispatcher dispatcher, string pipeName, string secret)
{
    private const int MaximumMessageSize = 1024 * 1024;
    private const string InspectionTitlePrefix = "[AI inspection] ";
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<Window, string> originalTitles = [];
    private DispatcherTimer? windowMarkerTimer;

    internal void Start()
    {
        dispatcher.BeginInvoke(StartWindowMarkers);
        _ = Task.Run(ServeAsync);
    }

    private void StartWindowMarkers()
    {
        MarkWindows();
        windowMarkerTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => MarkWindows(), dispatcher);
        windowMarkerTimer.Start();
    }

    private void MarkWindows()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (!originalTitles.ContainsKey(window))
            {
                originalTitles[window] = window.Title;
                window.Closed += (_, _) =>
                {
                    originalTitles.Remove(window);
                };
            }
            if (!window.Title.StartsWith(InspectionTitlePrefix, StringComparison.Ordinal))
                window.Title = InspectionTitlePrefix + window.Title;
        }
    }

    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                Log($"Waiting for connection on {pipeName}.");
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                Log("Client connected.");
                var requestBytes = await ReadFrameAsync(pipe, stop.Token).ConfigureAwait(false);
                Log($"Request read: {requestBytes.Length} bytes.");
                var response = await HandleAsync(Encoding.UTF8.GetString(requestBytes)).ConfigureAwait(false);
                var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response);
                await WriteFrameAsync(pipe, responseBytes, stop.Token).ConfigureAwait(false);
                Log($"Response written: {responseBytes.Length} bytes.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception exception) { Log($"Request failed: {exception}"); }
        }
    }

    private async Task<object> HandleAsync(string json)
    {
        try
        {
            var request = JsonSerializer.Deserialize<Request>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Invalid inspection request.");
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(request.Secret ?? "")))
                throw new UnauthorizedAccessException("Invalid inspection session secret.");

            return request.Operation switch
            {
                "roots" => await dispatcher.InvokeAsync(DescribeRoots),
                "visual_tree" => await dispatcher.InvokeAsync(() => DescribeTree(request.Arguments, TreeKind.Visual)),
                "logical_tree" => await dispatcher.InvokeAsync(() => DescribeTree(request.Arguments, TreeKind.Logical)),
                "find_elements" => await dispatcher.InvokeAsync(() => FindElements(request.Arguments)),
                "element_details" => await dispatcher.InvokeAsync(() => DescribeElement(request.Arguments)),
                "bindings" => await dispatcher.InvokeAsync(() => DescribeBindings(request.Arguments)),
                _ => throw new InvalidDataException($"Unknown inspection operation '{request.Operation}'.")
            };
        }
        catch (Exception exception)
        {
            Log($"Operation failed: {exception}");
            return new { error = exception.Message };
        }
    }

    private object DescribeRoots() => new
    {
        windows = Application.Current.Windows.Cast<Window>().Select((window, index) => new
        {
            windowIndex = index,
            visualRootId = $"v:{index}",
            logicalRootId = $"l:{index}",
            element = Describe(window, $"v:{index}", TreeKind.Visual)
        }).ToArray()
    };

    private object DescribeTree(JsonElement? arguments, TreeKind kind)
    {
        var rootId = GetString(arguments, "rootId");
        var maxDepth = GetInt(arguments, "maxDepth", 3, 0, 8);
        var maxChildren = GetInt(arguments, "maxChildren", 100, 1, 250);
        var roots = ResolveRoots(rootId, kind).Select(root => BuildNode(root.Element, root.Id, kind, maxDepth, maxChildren)).ToArray();
        return new { tree = kind.ToString().ToLowerInvariant(), maxDepth, maxChildren, roots };
    }

    private object DescribeElement(JsonElement? arguments)
    {
        var nodeId = GetRequiredString(arguments, "nodeId");
        var (element, kind) = ResolveNode(nodeId);
        var frameworkElement = element as FrameworkElement;
        return new
        {
            node = Describe(element, nodeId, kind),
            layout = frameworkElement is null ? null : new
            {
                actualWidth = frameworkElement.ActualWidth,
                actualHeight = frameworkElement.ActualHeight,
                margin = frameworkElement.Margin.ToString(),
                horizontalAlignment = frameworkElement.HorizontalAlignment.ToString(),
                verticalAlignment = frameworkElement.VerticalAlignment.ToString()
            },
            dataContextType = frameworkElement?.DataContext?.GetType().FullName,
            localBindings = GetBindings(element).ToArray()
        };
    }

    private object FindElements(JsonElement? arguments)
    {
        var query = GetRequiredString(arguments, "query");
        if (query.Length > 256) throw new InvalidDataException("query must be at most 256 characters.");
        var tree = string.Equals(GetString(arguments, "tree"), "logical", StringComparison.OrdinalIgnoreCase) ? TreeKind.Logical : TreeKind.Visual;
        var maxResults = GetInt(arguments, "maxResults", 50, 1, 100);
        const int maxNodes = 10_000;
        var matches = new List<object>();
        var pending = new Queue<(DependencyObject Element, string Id)>();
        foreach (var root in ResolveRoots(null, tree)) pending.Enqueue(root);

        var inspected = 0;
        while (pending.Count > 0 && inspected++ < maxNodes && matches.Count < maxResults)
        {
            var (element, id) = pending.Dequeue();
            if (IsMatch(element, query)) matches.Add(Describe(element, id, tree));
            foreach (var (child, index) in GetChildren(element, tree).Select((child, index) => (child, index)))
                pending.Enqueue((child, $"{id}/{index}"));
        }
        return new { tree = tree.ToString().ToLowerInvariant(), query, inspectedNodes = inspected, matches, truncated = pending.Count > 0 };
    }

    private object DescribeBindings(JsonElement? arguments)
    {
        var nodeId = GetRequiredString(arguments, "nodeId");
        var (element, _) = ResolveNode(nodeId);
        return new { nodeId, bindings = GetBindings(element).ToArray() };
    }

    private object BuildNode(DependencyObject element, string id, TreeKind kind, int remainingDepth, int maxChildren)
    {
        var children = GetChildren(element, kind).ToArray();
        var visibleChildren = remainingDepth == 0
            ? Array.Empty<object>()
            : children.Take(maxChildren).Select((child, index) =>
                BuildNode(child, $"{id}/{index}", kind, remainingDepth - 1, maxChildren)).ToArray();
        return new
        {
            element = Describe(element, id, kind),
            children = visibleChildren,
            childCount = children.Length,
            childrenTruncated = remainingDepth == 0 ? children.Length > 0 : children.Length > maxChildren
        };
    }

    private static object Describe(DependencyObject element, string id, TreeKind kind) => new
    {
        id,
        tree = kind.ToString().ToLowerInvariant(),
        type = element.GetType().FullName,
        name = element is FrameworkElement frameworkElement ? frameworkElement.Name : null,
        automationId = element is FrameworkElement fe ? AutomationProperties.GetAutomationId(fe) : null,
        visibility = element is UIElement uiElement ? uiElement.Visibility.ToString() : null,
        text = GetDisplayText(element),
        visualChildren = GetChildren(element, TreeKind.Visual).Count(),
        logicalChildren = GetChildren(element, TreeKind.Logical).Count()
    };

    private static string? GetDisplayText(DependencyObject element) => element switch
    {
        TextBlock textBlock => textBlock.Text,
        TextBox textBox => textBox.Text,
        ContentControl { Content: string content } => content,
        _ => null
    };

    private static bool IsMatch(DependencyObject element, string query)
    {
        var frameworkElement = element as FrameworkElement;
        return new[]
        {
            frameworkElement?.Name,
            frameworkElement is null ? null : AutomationProperties.GetAutomationId(frameworkElement),
            element.GetType().FullName,
            GetDisplayText(element)
        }.Any(value => !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<DependencyObject> GetChildren(DependencyObject element, TreeKind kind)
    {
        if (kind == TreeKind.Visual)
        {
            if (element is not Visual and not Visual3D) return [];
            return Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(element)).Select(index => VisualTreeHelper.GetChild(element, index));
        }

        try { return LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>().ToArray(); }
        catch (InvalidOperationException) { return []; }
    }

    private IEnumerable<(DependencyObject Element, string Id)> ResolveRoots(string? rootId, TreeKind kind)
    {
        if (!string.IsNullOrWhiteSpace(rootId))
        {
            var (element, resolvedKind) = ResolveNode(rootId);
            if (resolvedKind != kind) throw new InvalidDataException($"rootId '{rootId}' is not a {kind.ToString().ToLowerInvariant()} tree id.");
            return [(element, rootId)];
        }

        return Application.Current.Windows.Cast<Window>().Select((window, index) => ((DependencyObject)window, $"{Prefix(kind)}:{index}"));
    }

    private (DependencyObject Element, TreeKind Kind) ResolveNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 512)
            throw new InvalidDataException("nodeId is required and must be at most 512 characters.");
        var separator = nodeId.IndexOf(':');
        if (separator != 1) throw new InvalidDataException($"Invalid nodeId '{nodeId}'.");
        var kind = nodeId[0] switch { 'v' => TreeKind.Visual, 'l' => TreeKind.Logical, _ => throw new InvalidDataException($"Invalid nodeId '{nodeId}'.") };
        var parts = nodeId[(separator + 1)..].Split('/', StringSplitOptions.None);
        if (!int.TryParse(parts[0], out var windowIndex) || windowIndex < 0 || windowIndex >= Application.Current.Windows.Count)
            throw new InvalidDataException($"nodeId '{nodeId}' does not identify a current WPF window.");
        DependencyObject current = Application.Current.Windows[windowIndex];
        foreach (var part in parts.Skip(1))
        {
            if (!int.TryParse(part, out var childIndex) || childIndex < 0) throw new InvalidDataException($"Invalid nodeId '{nodeId}'.");
            current = GetChildren(current, kind).ElementAtOrDefault(childIndex)
                ?? throw new InvalidDataException($"nodeId '{nodeId}' is no longer valid because the tree changed.");
        }
        return (current, kind);
    }

    private static IEnumerable<object> GetBindings(DependencyObject element)
    {
        var bindings = new List<object>();
        var localValues = element.GetLocalValueEnumerator();
        while (localValues.MoveNext())
        {
            var entry = localValues.Current;
            var expression = BindingOperations.GetBindingExpressionBase(element, entry.Property);
            if (expression is null) continue;
            var binding = expression.ParentBindingBase as Binding;
            bindings.Add(new
            {
                targetProperty = entry.Property.Name,
                bindingType = expression.ParentBindingBase.GetType().Name,
                path = binding?.Path?.Path,
                mode = binding?.Mode.ToString(),
                updateSourceTrigger = binding?.UpdateSourceTrigger.ToString(),
                status = expression.Status.ToString()
            });
        }
        return bindings;
    }

    private static string GetRequiredString(JsonElement? arguments, string name) =>
        GetString(arguments, name) ?? throw new InvalidDataException($"{name} is required.");

    private static string? GetString(JsonElement? arguments, string name) =>
        arguments is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt(JsonElement? arguments, string name, int fallback, int minimum, int maximum) =>
        arguments is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number)
            ? Math.Clamp(number, minimum, maximum)
            : fallback;

    private static char Prefix(TreeKind kind) => kind == TreeKind.Visual ? 'v' : 'l';

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumMessageSize) throw new InvalidDataException($"Inspection response exceeds {MaximumMessageSize} bytes.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaximumMessageSize) throw new InvalidDataException($"Invalid inspection request length: {length}.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private sealed record Request(string? Secret, string? Operation, JsonElement? Arguments);
    private enum TreeKind { Visual, Logical }

    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), $"WpfInspector.Agent-{Environment.ProcessId}.log"), $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}"); }
        catch { }
    }
}
