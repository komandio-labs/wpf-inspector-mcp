using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
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

    internal void Stop()
    {
        stop.Cancel();
        dispatcher.BeginInvoke(() =>
        {
            windowMarkerTimer?.Stop();
            foreach (var (window, title) in originalTitles.ToArray())
                if (!window.Dispatcher.HasShutdownStarted) window.Title = title;
            originalTitles.Clear();
        });
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
                "interactive_elements" => await dispatcher.InvokeAsync(() => DescribeInteractiveElements(request.Arguments)),
                "surfaces" => await dispatcher.InvokeAsync(DescribeSurfaces),
                "interact" => await dispatcher.InvokeAsync(() => Interact(request.Arguments)),
                "wait_for_state" => await WaitForStateAsync(request.Arguments),
                "run_workflow" => await RunWorkflowAsync(request.Arguments),
                "stop" => await dispatcher.InvokeAsync(() => { Stop(); return new { stopped = true }; }),
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
        }).ToArray(),
        popupRoots = GetVisualRoots()
            .Where(root => root.Id.StartsWith("p:", StringComparison.Ordinal))
            .Select(root => new { rootId = root.Id, element = Describe(root.Element, root.Id, TreeKind.Visual) })
            .ToArray()
    };

    private object DescribeTree(JsonElement? arguments, TreeKind kind)
    {
        ValidateExpectedRevision(arguments);
        var rootId = GetString(arguments, "rootId");
        var maxDepth = GetInt(arguments, "maxDepth", 3, 0, 8);
        var maxChildren = GetInt(arguments, "maxChildren", 100, 1, 250);
        var roots = ResolveRoots(rootId, kind).Select(root => BuildNode(root.Element, root.Id, kind, maxDepth, maxChildren)).ToArray();
        return new { uiRevision = GetUiRevision(), tree = kind.ToString().ToLowerInvariant(), maxDepth, maxChildren, roots };
    }

    private object DescribeElement(JsonElement? arguments)
    {
        ValidateExpectedRevision(arguments);
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
            bounds = GetBounds(frameworkElement),
            state = GetState(element),
            capabilities = GetCapabilities(element),
            localBindings = GetBindings(element).ToArray()
        };
    }

    private object DescribeInteractiveElements(JsonElement? arguments)
    {
        ValidateExpectedRevision(arguments);
        var query = GetString(arguments, "query");
        var maxResults = GetInt(arguments, "maxResults", 100, 1, 250);
        var results = EnumerateVisualElements()
            .Where(item => item.Element is UIElement ui && ui.IsVisible && ui.IsEnabled && GetCapabilities(item.Element).Length > 0)
            .Where(item => string.IsNullOrWhiteSpace(query) || IsMatch(item.Element, query))
            .Take(maxResults)
            .Select(item => new { locator = Locator(item.Element, item.Id), node = Describe(item.Element, item.Id, TreeKind.Visual), bounds = GetBounds(item.Element as FrameworkElement), capabilities = GetCapabilities(item.Element) })
            .ToArray();
        return new { uiRevision = GetUiRevision(), elements = results, truncated = results.Length == maxResults };
    }

    private object DescribeSurfaces() => new
    {
        windows = Application.Current.Windows.Cast<Window>().Select((window, index) => new { windowIndex = index, title = window.Title, isVisible = window.IsVisible, isActive = window.IsActive, rootId = $"v:{index}" }).ToArray(),
        presentationRoots = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual).Where(root => root is not null).Select(root => new { type = root!.GetType().FullName, isPopup = root.GetType().Name.Contains("Popup", StringComparison.OrdinalIgnoreCase) }).ToArray()
    };

    private object Interact(JsonElement? arguments)
    {
        ValidateExpectedRevision(arguments);
        var (element, id) = ResolveLocator(arguments);
        var requestedAction = GetString(arguments, "action")?.Trim() ?? "auto";
        var value = GetString(arguments, "value");
        var action = requestedAction.Equals("auto", StringComparison.OrdinalIgnoreCase) ? GetCapabilities(element).FirstOrDefault() ?? "" : requestedAction;
        if (string.IsNullOrEmpty(action)) throw new InvalidOperationException("The target has no supported semantic interaction.");
        switch (action.ToLowerInvariant())
        {
            case "invoke": Invoke(element); break;
            case "select": Select(element, value); break;
            case "settext": SetText(element, value ?? string.Empty); break;
            case "setrangevalue": SetRangeValue(element, value); break;
            case "toggle": Toggle(element, value); break;
            case "setdate": if (element is DatePicker datePicker && DateTime.TryParse(value, out var date)) datePicker.SelectedDate = date; else throw new InvalidOperationException("setDate requires a DatePicker and ISO date value."); break;
            case "focus": if (element is UIElement focusable && focusable.Focusable) focusable.Focus(); else throw new InvalidOperationException("focus requires a focusable UIElement."); break;
            case "sendkey": SendKey(element, value); break;
            case "expand":
                if (element is TreeViewItem treeItem) treeItem.IsExpanded = true;
                else if (element is Expander expander) expander.IsExpanded = true;
                else throw new InvalidOperationException("expand requires a TreeViewItem or Expander.");
                break;
            case "collapse":
                if (element is TreeViewItem collapsible) collapsible.IsExpanded = false;
                else if (element is Expander expander) expander.IsExpanded = false;
                else throw new InvalidOperationException("collapse requires a TreeViewItem or Expander.");
                break;
            default: throw new InvalidOperationException($"Unsupported interaction action '{requestedAction}'.");
        }
        return new { uiRevision = GetUiRevision(), nodeId = id, action, strategyUsed = action == "invoke" ? "wpf.routed-command-or-click" : "wpf.direct-control", bounds = GetBounds(element as FrameworkElement), state = GetState(element) };
    }

    private async Task<object> WaitForStateAsync(JsonElement? arguments)
    {
        ValidateExpectedRevision(arguments);
        var timeoutMs = GetInt(arguments, "timeoutMs", 2_000, 50, 30_000);
        var condition = GetString(arguments, "condition") ?? "exists";
        var expected = GetString(arguments, "expectedValue");
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        do
        {
            var result = await dispatcher.InvokeAsync(() => TryMatchState(arguments, condition, expected));
            if (result.Matched) return new { matched = true, condition, result.NodeId, state = result.State };
            await Task.Delay(75).ConfigureAwait(false);
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for condition '{condition}'.");
    }

    private async Task<object> RunWorkflowAsync(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("steps must be an array.");
        if (steps.GetArrayLength() is < 1 or > 25) throw new InvalidDataException("steps must contain between 1 and 25 entries.");
        var trace = new List<object>();
        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            var started = DateTime.UtcNow;
            try
            {
                var kind = GetString(step, "kind") ?? throw new InvalidDataException("Each workflow step requires kind.");
                object result;
                if (kind.Equals("interact", StringComparison.OrdinalIgnoreCase)) result = await dispatcher.InvokeAsync(() => Interact(step));
                else if (kind.Equals("wait", StringComparison.OrdinalIgnoreCase)) result = await WaitForStateAsync(step);
                else if (kind.Equals("assert", StringComparison.OrdinalIgnoreCase))
                {
                    var check = await dispatcher.InvokeAsync(() => TryMatchState(step, GetString(step, "condition") ?? "exists", GetString(step, "expectedValue")));
                    if (!check.Matched) throw new InvalidOperationException($"Workflow assertion failed: {GetString(step, "condition") ?? "exists"}.");
                    result = new { asserted = true, check.NodeId };
                }
                else throw new InvalidDataException($"Unknown workflow step kind '{kind}'.");
                trace.Add(new { index, kind, durationMs = (DateTime.UtcNow - started).TotalMilliseconds, result });
            }
            catch (Exception exception) { return new { completed = false, failedStep = index, failureMessage = exception.Message, trace }; }
            index++;
        }
        return new { completed = true, steps = trace };
    }

    private object FindElements(JsonElement? arguments)
    {
        ValidateExpectedRevision(arguments);
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
        return new { uiRevision = GetUiRevision(), tree = tree.ToString().ToLowerInvariant(), query, inspectedNodes = inspected, matches, truncated = pending.Count > 0 };
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

    private IEnumerable<(DependencyObject Element, string Id)> EnumerateVisualElements()
    {
        var queue = new Queue<(DependencyObject Element, string Id)>(ResolveRoots(null, TreeKind.Visual));
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            yield return current;
            foreach (var (child, index) in GetChildren(current.Element, TreeKind.Visual).Select((child, index) => (child, index))) queue.Enqueue((child, $"{current.Id}/{index}"));
        }
    }

    private (DependencyObject Element, string Id) ResolveLocator(JsonElement? arguments)
    {
        var nodeId = GetString(arguments, "nodeId");
        if (!string.IsNullOrWhiteSpace(nodeId)) return (ResolveNode(nodeId).Element, nodeId);
        if (arguments is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("locator", out var locator) || locator.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Provide nodeId or locator.");
        var automationId = GetString(locator, "automationId");
        var name = GetString(locator, "name");
        var query = GetString(locator, "query");
        var all = EnumerateVisualElements();
        var matches = !string.IsNullOrWhiteSpace(automationId)
            ? all.Where(item => item.Element is FrameworkElement fe && string.Equals(AutomationProperties.GetAutomationId(fe), automationId, StringComparison.Ordinal)).ToArray()
            : !string.IsNullOrWhiteSpace(name)
                ? all.Where(item => item.Element is FrameworkElement named && string.Equals(named.Name, name, StringComparison.Ordinal)).ToArray()
                : !string.IsNullOrWhiteSpace(query)
                    ? all.Where(item => IsMatch(item.Element, query)).ToArray()
                    : throw new InvalidDataException("locator must contain automationId, name, or query.");
        if (matches.Length == 0) throw new InvalidDataException("No live WPF element matched the locator.");
        if (matches.Length > 1) throw new InvalidDataException("The locator is ambiguous. Use automationId, name, or a nodeId. Matches: " + string.Join(", ", matches.Take(5).Select(item => item.Id)));
        return matches[0];
    }

    private void ValidateExpectedRevision(JsonElement? arguments)
    {
        var expected = GetString(arguments, "expectedRevision");
        if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, GetUiRevision(), StringComparison.Ordinal))
            throw new InvalidDataException("The WPF tree changed since the supplied uiRevision. Refresh the relevant tree or locator before acting.");
    }

    private string GetUiRevision()
    {
        var hash = new HashCode();
        foreach (var item in EnumerateVisualElements().Take(10_000))
        {
            hash.Add(item.Element.GetType().FullName, StringComparer.Ordinal);
            hash.Add((item.Element as FrameworkElement)?.Name, StringComparer.Ordinal);
            hash.Add(item.Element is UIElement ui && ui.IsVisible);
            hash.Add(GetChildren(item.Element, TreeKind.Visual).Count());
        }
        return hash.ToHashCode().ToString("X8");
    }

    private static string[] GetCapabilities(DependencyObject element)
    {
        var values = new List<string>();
        if (element is ButtonBase or MenuItem || element is ICommandSource { Command: not null }) values.Add("invoke");
        if (element is Selector) values.Add("select");
        if (element is TextBox or PasswordBox) values.Add("setText");
        if (element is DatePicker) values.Add("setDate");
        if (element is RangeBase) values.Add("setRangeValue");
        if (element is ToggleButton) values.Add("toggle");
        if (element is TreeViewItem or Expander) { values.Add("expand"); values.Add("collapse"); }
        if (element is UIElement { Focusable: true }) values.Add("focus");
        if (element is UIElement) values.Add("sendKey");
        var peer = element is FrameworkElement framework ? FrameworkElementAutomationPeer.CreatePeerForElement(framework) ?? FrameworkElementAutomationPeer.FromElement(framework) : null;
        if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider && !values.Contains("invoke")) values.Add("invoke");
        if (peer?.GetPattern(PatternInterface.Value) is IValueProvider && !values.Contains("setText")) values.Add("setText");
        if (peer?.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider && !values.Contains("setRangeValue")) values.Add("setRangeValue");
        if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider && !values.Contains("toggle")) values.Add("toggle");
        return values.ToArray();
    }

    private static object Locator(DependencyObject element, string nodeId) => new { nodeId, automationId = element is FrameworkElement fe ? AutomationProperties.GetAutomationId(fe) : null, name = (element as FrameworkElement)?.Name };

    private static object? GetBounds(FrameworkElement? element)
    {
        if (element is null || !element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
        var window = Window.GetWindow(element);
        if (window is null) return null;
        var clientTopLeft = element.TranslatePoint(new Point(0, 0), window);
        var screenTopLeft = element.PointToScreen(new Point(0, 0));
        var frame = new NativeRect();
        GetWindowRect(new WindowInteropHelper(window).Handle, out frame);
        return new
        {
            windowClient = new { x = clientTopLeft.X, y = clientTopLeft.Y, width = element.ActualWidth, height = element.ActualHeight },
            screen = new { x = screenTopLeft.X, y = screenTopLeft.Y, width = element.ActualWidth, height = element.ActualHeight },
            windowFrame = new { x = screenTopLeft.X - frame.Left, y = screenTopLeft.Y - frame.Top, width = element.ActualWidth, height = element.ActualHeight }
        };
    }

    private static object GetState(DependencyObject element) => new
    {
        isVisible = element is UIElement ui && ui.IsVisible,
        isEnabled = element is UIElement enabled && enabled.IsEnabled,
        isHitTestVisible = element is UIElement hit && hit.IsHitTestVisible,
        isKeyboardFocused = element is UIElement focused && focused.IsKeyboardFocused,
        isChecked = element is ToggleButton toggle ? toggle.IsChecked : null,
        text = element is TextBox textBox ? textBox.Text : GetDisplayText(element),
        value = element is RangeBase range ? (double?)range.Value : null
    };

    private static void Invoke(DependencyObject element)
    {
        if (element is ButtonBase button) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return; }
        if (element is MenuItem menu) { menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); return; }
        if (element is ICommandSource { Command: { } command } source)
        {
            if (!command.CanExecute(source.CommandParameter)) throw new InvalidOperationException("The target command cannot execute.");
            command.Execute(source.CommandParameter); return;
        }
        if (GetPeer(element)?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) { invoke.Invoke(); return; }
        throw new InvalidOperationException("invoke requires a button, menu item, command source, or UI Automation invoke provider.");
    }

    private static AutomationPeer? GetPeer(DependencyObject element) => element is FrameworkElement framework
        ? FrameworkElementAutomationPeer.CreatePeerForElement(framework) ?? FrameworkElementAutomationPeer.FromElement(framework)
        : null;

    private static void SendKey(DependencyObject element, string? value)
    {
        if (element is not UIElement ui || !Enum.TryParse<Key>(value, true, out var key)) throw new InvalidOperationException("sendKey requires a UIElement and a valid WPF Key value.");
        ui.Focus();
        var source = PresentationSource.FromVisual(ui as Visual) ?? throw new InvalidOperationException("The target is not connected to a presentation source.");
        ui.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        ui.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    private static void SetText(DependencyObject element, string value)
    {
        if (element is TextBox textBox) { textBox.Text = value; return; }
        if (element is PasswordBox passwordBox) { passwordBox.Password = value; return; }
        if (GetPeer(element)?.GetPattern(PatternInterface.Value) is IValueProvider provider && !provider.IsReadOnly) { provider.SetValue(value); return; }
        throw new InvalidOperationException("setText requires a writable TextBox, PasswordBox, or UI Automation value provider.");
    }

    private static void SetRangeValue(DependencyObject element, string? value)
    {
        if (!double.TryParse(value, out var number)) throw new InvalidOperationException("setRangeValue requires a numeric value.");
        if (element is RangeBase range) { range.Value = Math.Clamp(number, range.Minimum, range.Maximum); return; }
        if (GetPeer(element)?.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider provider && !provider.IsReadOnly) { provider.SetValue(number); return; }
        throw new InvalidOperationException("setRangeValue requires a writable RangeBase or UI Automation range provider.");
    }

    private static void Toggle(DependencyObject element, string? value)
    {
        if (element is ToggleButton toggle) { toggle.IsChecked = value is null ? !(toggle.IsChecked ?? false) : bool.Parse(value); return; }
        if (GetPeer(element)?.GetPattern(PatternInterface.Toggle) is IToggleProvider provider) { provider.Toggle(); return; }
        throw new InvalidOperationException("toggle requires a ToggleButton or UI Automation toggle provider.");
    }

    private static void Select(DependencyObject element, string? value)
    {
        if (element is not Selector selector) throw new InvalidOperationException("select requires a Selector.");
        var item = selector.Items.Cast<object>().FirstOrDefault(candidate =>
            string.Equals(candidate?.ToString(), value, StringComparison.OrdinalIgnoreCase) ||
            candidate is ContentControl { Content: string content } && string.Equals(content, value, StringComparison.OrdinalIgnoreCase) ||
            candidate is HeaderedContentControl { Header: string header } && string.Equals(header, value, StringComparison.OrdinalIgnoreCase));
        if (item is null) throw new InvalidOperationException($"No selector item matched '{value}'.");
        selector.SelectedItem = item;
    }

    private (bool Matched, string? NodeId, object? State) TryMatchState(JsonElement? arguments, string condition, string? expected)
    {
        try
        {
            var (element, id) = ResolveLocator(arguments);
            var ui = element as UIElement;
            var matched = condition.ToLowerInvariant() switch
            {
                "exists" => true,
                "visible" => ui?.IsVisible is true,
                "hidden" => ui?.IsVisible is false,
                "enabled" => ui?.IsEnabled is true,
                "disabled" => ui?.IsEnabled is false,
                "textequals" => string.Equals(GetDisplayText(element), expected, StringComparison.Ordinal),
                "checked" => element is ToggleButton toggle && string.Equals(toggle.IsChecked?.ToString(), expected, StringComparison.OrdinalIgnoreCase),
                "valueequals" => element is RangeBase range && double.TryParse(expected, out var expectedValue) && Math.Abs(range.Value - expectedValue) < 0.001,
                "focused" => ui?.IsKeyboardFocused is true,
                "validationhaserror" => element is FrameworkElement framework && Validation.GetHasError(framework),
                _ => throw new InvalidDataException($"Unknown wait condition '{condition}'.")
            };
            return (matched, id, GetState(element));
        }
        catch (InvalidDataException) when (condition.Equals("gone", StringComparison.OrdinalIgnoreCase)) { return (true, null, null); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect rectangle);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

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

        if (kind == TreeKind.Logical)
            return Application.Current.Windows.Cast<Window>().Select((window, index) => ((DependencyObject)window, $"l:{index}"));

        return GetVisualRoots();
    }

    private static IEnumerable<(DependencyObject Element, string Id)> GetVisualRoots()
    {
        foreach (var (window, index) in Application.Current.Windows.Cast<Window>().Select((window, index) => (window, index)))
            yield return (window, $"v:{index}");

        var popupIndex = 0;
        foreach (var root in PresentationSource.CurrentSources.Cast<PresentationSource>()
                     .Select(source => source.RootVisual)
                     .OfType<DependencyObject>()
                     .Where(root => root is not Window))
            yield return (root, $"p:{popupIndex++}");
    }

    private (DependencyObject Element, TreeKind Kind) ResolveNode(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 512)
            throw new InvalidDataException("nodeId is required and must be at most 512 characters.");
        var separator = nodeId.IndexOf(':');
        if (separator != 1) throw new InvalidDataException($"Invalid nodeId '{nodeId}'.");
        var prefix = nodeId[0];
        var kind = prefix switch { 'v' or 'p' => TreeKind.Visual, 'l' => TreeKind.Logical, _ => throw new InvalidDataException($"Invalid nodeId '{nodeId}'.") };
        var parts = nodeId[(separator + 1)..].Split('/', StringSplitOptions.None);
        if (!int.TryParse(parts[0], out var rootIndex) || rootIndex < 0)
            throw new InvalidDataException($"nodeId '{nodeId}' does not identify a current WPF root.");
        DependencyObject current;
        if (prefix == 'p')
        {
            current = GetVisualRoots().Where(root => root.Id.StartsWith("p:", StringComparison.Ordinal)).ElementAtOrDefault(rootIndex).Element
                ?? throw new InvalidDataException($"nodeId '{nodeId}' does not identify a current popup root.");
        }
        else if (rootIndex < Application.Current.Windows.Count)
            current = Application.Current.Windows[rootIndex];
        else
            throw new InvalidDataException($"nodeId '{nodeId}' does not identify a current WPF window.");
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
