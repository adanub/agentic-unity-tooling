using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

namespace Adanub.UnityMcp.Editor.Commands
{
    /// <summary>
    /// UI Toolkit visual-tree inspection: what an editor window's UI actually resolved to, as
    /// numbers rather than pixels.
    /// <para>
    /// This is the counterpart to a screenshot for editor-UI work. A screenshot shows what a control
    /// looks like; it cannot show whether an element owns a property inline or is still being driven
    /// by a stylesheet, what a zero-height element's box actually computed to, or that the element
    /// painting the wrong thing is one nobody thought to look at. All three are the usual causes of
    /// a "fix" that changes nothing, so the dump reports resolved AND inline values, enumerates
    /// every child rather than a list of expected classes, and states plainly when it truncated.
    /// </para>
    /// </summary>
    public static class UiToolkitCommands
    {
        private const int DefaultMaxDepth = 12;
        private const int DefaultMaxElements = 400;

        /// <summary>Property groups a caller can ask for, in the order they are printed.</summary>
        private static readonly string[] AllGroups = { "geometry", "box", "display", "colour", "text", "image" };

        private static readonly string[] DefaultGroups = { "geometry", "box", "display", "text" };

        // Reflected members are cached per type, and each may legitimately find nothing — most
        // windows have no lock toggle, and most elements no label.
        private static readonly Dictionary<Type, FieldInfo> _rootFields = new();
        private static readonly Dictionary<Type, PropertyInfo> _lockProperties = new();
        private static readonly Dictionary<Type, PropertyInfo> _labelProperties = new();

        [McpRoute("uitk/windows", "Open EditorWindows and their UI Toolkit roots. Args: none.")]
        public static object Windows(JObject args)
        {
            var list = new List<object>();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null) continue;
                var root = SafeRoot(window);
                list.Add(new Dictionary<string, object>
                {
                    { "type", window.GetType().Name },
                    { "fullType", window.GetType().FullName },
                    { "title", SafeTitle(window) },
                    { "hasRoot", root is not null },
                    { "childCount", root?.hierarchy.childCount ?? 0 },
                    { "locked", IsLocked(window) },
                    { "focused", EditorWindow.focusedWindow == window },
                });
            }

            return new Dictionary<string, object> { { "count", list.Count }, { "windows", list } };
        }

        [McpRoute("uitk/repaint",
            "Ask editor windows to redraw, so a retained UI Toolkit tree rebuilds before it is dumped. Args: window (type name, default all). Call this after changing the selection, then dump on a following call — the redraw happens on a later editor frame, not within this one.")]
        public static object Repaint(JObject args)
        {
            // A window's visual tree is only rebuilt when the window draws. An editor running
            // unfocused has no reason to redraw after a programmatic selection change, so a dump
            // taken straight afterwards reports the PREVIOUS selection's tree — or an empty one —
            // which reads exactly like a genuine finding.
            var windows = ResolveWindows(args.Value<string>("window") ?? "*", out var error);
            if (error is not null)
                return new { error };

            var repainted = new List<string>();
            foreach (var window in windows)
            {
                try
                {
                    window.Repaint();
                    repainted.Add(window.GetType().Name);
                }
                catch (Exception)
                {
                    // A window mid-teardown cannot repaint; that is not a failure of the request.
                }
            }

            return new Dictionary<string, object>
            {
                { "repainted", repainted.ToArray() },
                { "note", "Redraw lands on a later editor frame — dump on a following call, not this one." },
            };
        }

        [McpRoute("uitk/expand-inspector",
            "Expand (or collapse) the Inspector's component foldouts for the current selection, so their contents are actually built and can be dumped. Args: expanded (bool, default true), types (string[] of component type names; default all).")]
        public static object ExpandInspector(JObject args)
        {
            // A collapsed component builds no inspector content at all, so a dump of it reports
            // nothing — which looks identical to a component whose fields failed to draw.
            var expanded = args.Value<bool?>("expanded") ?? true;
            var wanted = args["types"] is JArray a
                ? a.Select(t => t.ToString()).ToArray()
                : Array.Empty<string>();

            var touched = new List<string>();
            foreach (var obj in Selection.objects)
            {
                if (obj == null) continue;
                var targets = obj is GameObject go
                    ? go.GetComponents<Component>().Cast<UnityEngine.Object>()
                    : new[] { obj };
                foreach (var target in targets)
                {
                    if (target == null) continue;
                    var name = target.GetType().Name;
                    if (wanted.Length > 0 && !wanted.Contains(name)) continue;
                    try
                    {
                        InternalEditorUtility.SetIsInspectorExpanded(target, expanded);
                        touched.Add(name);
                    }
                    catch (Exception)
                    {
                        // Not every object supports the expansion flag; skip rather than fail.
                    }
                }
            }

            if (touched.Count > 0)
            {
                try
                {
                    ActiveEditorTracker.sharedTracker.ForceRebuild();
                }
                catch (Exception)
                {
                    // Rebuild is best-effort; the caller still repaints before dumping.
                }
            }

            return new Dictionary<string, object>
            {
                { "expanded", expanded },
                { "affected", touched.ToArray() },
                { "note", "Repaint, then dump on a following call." },
            };
        }

        [McpRoute("uitk/dump",
            "Dump an editor window's UI Toolkit visual tree with resolved + inline style. Args: window (type name, default focused; '*' for all), selector ('.class' or 'TypeName' to root the dump, optional), properties (string[] of geometry|box|display|colour|text|image), maxDepth (12), maxElements (400), includeHidden (true).")]
        public static object Dump(JObject args)
        {
            var windowFilter = args.Value<string>("window");
            var selector = args.Value<string>("selector");
            var maxDepth = args.Value<int?>("maxDepth") ?? DefaultMaxDepth;
            var maxElements = args.Value<int?>("maxElements") ?? DefaultMaxElements;
            var includeHidden = args.Value<bool?>("includeHidden") ?? true;

            var groups = args["properties"] is JArray requested && requested.Count > 0
                ? requested.Select(t => t.ToString().ToLowerInvariant()).ToArray()
                : DefaultGroups;
            var unknown = groups.Where(g => !AllGroups.Contains(g)).ToArray();
            if (unknown.Length > 0)
                return new { error = $"Unknown property group(s): {string.Join(", ", unknown)}. Valid: {string.Join(", ", AllGroups)}." };

            var windows = ResolveWindows(windowFilter, out var resolveError);
            if (resolveError is not null)
                return new { error = resolveError };

            var state = new DumpState { MaxDepth = maxDepth, MaxElements = maxElements, IncludeHidden = includeHidden, Groups = groups };
            var sb = new StringBuilder();
            var dumped = new List<object>();

            foreach (var window in windows)
            {
                var root = SafeRoot(window);
                if (root is null)
                {
                    // Said out loud rather than skipped: an empty answer for a window the caller
                    // named reads exactly like "the tree did not contain what I searched for".
                    sb.AppendLine($"WINDOW {window.GetType().Name} '{SafeTitle(window)}'" +
                                  "  [no UI Toolkit root — an IMGUI window, or one that has not drawn yet]");
                    dumped.Add(new Dictionary<string, object>
                    {
                        { "type", window.GetType().Name },
                        { "hasRoot", false },
                        { "matchedRoots", 0 },
                    });
                    continue;
                }

                var roots = selector is null ? new List<VisualElement> { root } : Match(root, selector);
                sb.AppendLine($"WINDOW {window.GetType().Name} '{SafeTitle(window)}'" +
                              $"{(IsLocked(window) is true ? "  [LOCKED — showing a pinned object, not the selection]" : "")}");
                if (roots.Count == 0)
                    sb.AppendLine($"  no element matches selector '{selector}'");

                foreach (var element in roots)
                    Walk(sb, element, 1, state);

                dumped.Add(new Dictionary<string, object>
                {
                    { "type", window.GetType().Name },
                    { "locked", IsLocked(window) },
                    { "matchedRoots", roots.Count },
                });
            }

            // Truncation is reported rather than left to be inferred from a short dump: a silently
            // clipped tree reads exactly like a tree that did not contain what was being looked for.
            if (state.DepthTruncated > 0)
                sb.AppendLine($"[truncated] {state.DepthTruncated} subtree(s) cut at maxDepth={maxDepth}");
            if (state.ElementsTruncated)
                sb.AppendLine($"[truncated] element cap {maxElements} reached — raise maxElements or narrow selector");
            if (!includeHidden && state.HiddenSkipped > 0)
                sb.AppendLine($"[filtered] {state.HiddenSkipped} element(s) with display:None omitted");

            return new Dictionary<string, object>
            {
                { "windows", dumped },
                { "elements", state.Emitted },
                { "depthTruncated", state.DepthTruncated },
                { "elementsTruncated", state.ElementsTruncated },
                { "text", sb.ToString() },
            };
        }

        private sealed class DumpState
        {
            public int MaxDepth;
            public int MaxElements;
            public bool IncludeHidden;
            public string[] Groups;
            public int Emitted;
            public int DepthTruncated;
            public int HiddenSkipped;
            public bool ElementsTruncated;
        }

        private static List<EditorWindow> ResolveWindows(string filter, out string error)
        {
            error = null;
            var all = Resources.FindObjectsOfTypeAll<EditorWindow>().Where(w => w != null).ToList();

            if (filter == "*")
                return all;

            if (string.IsNullOrEmpty(filter))
            {
                var focused = EditorWindow.focusedWindow;
                if (focused != null)
                    return new List<EditorWindow> { focused };
                error = "No focused window; pass 'window' with a type name. Open windows: " +
                        string.Join(", ", all.Select(w => w.GetType().Name).Distinct().OrderBy(n => n));
                return null;
            }

            var matches = all.Where(w =>
                string.Equals(w.GetType().Name, filter, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.GetType().FullName, filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = all.Where(w => w.GetType().Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                error = $"No open window matches '{filter}'. Open windows: " +
                        string.Join(", ", all.Select(w => w.GetType().Name).Distinct().OrderBy(n => n));
            return matches;
        }

        /// <summary>
        /// Elements matching a selector: ".name" by USS class, otherwise by element type name. Both
        /// return EVERY match — a UI usually contains several instances of the thing under test, and
        /// silently taking the first is how the wrong one gets measured.
        /// </summary>
        private static List<VisualElement> Match(VisualElement root, string selector)
        {
            var results = new List<VisualElement>();
            var byClass = selector.StartsWith(".", StringComparison.Ordinal);
            var needle = byClass ? selector[1..] : selector;

            void Recurse(VisualElement e)
            {
                var hit = byClass
                    ? e.ClassListContains(needle)
                    : string.Equals(e.GetType().Name, needle, StringComparison.OrdinalIgnoreCase);
                if (hit) results.Add(e);
                for (var i = 0; i < e.hierarchy.childCount; i++)
                    Recurse(e.hierarchy[i]);
            }

            Recurse(root);
            return results;
        }

        private static void Walk(StringBuilder sb, VisualElement e, int depth, DumpState state)
        {
            if (state.Emitted >= state.MaxElements)
            {
                state.ElementsTruncated = true;
                return;
            }

            var hidden = e.resolvedStyle.display == DisplayStyle.None;
            if (hidden && !state.IncludeHidden)
            {
                state.HiddenSkipped++;
                return;
            }

            var indent = new string(' ', depth * 2);
            sb.Append(indent).Append(Describe(e));
            foreach (var group in state.Groups)
                sb.Append(Report(e, group));
            sb.AppendLine();
            state.Emitted++;

            if (depth >= state.MaxDepth)
            {
                if (e.hierarchy.childCount > 0)
                {
                    state.DepthTruncated++;
                    sb.Append(indent).AppendLine($"  … {e.hierarchy.childCount} child(ren) not shown (maxDepth)");
                }

                return;
            }

            // hierarchy, not Children(): the public enumeration walks the CONTENT container, so a
            // composite control's own chrome — a foldout's toggle and caret, a field's label — is
            // invisible to it. Those elements are routinely the ones misbehaving, and a probe that
            // cannot see them reports the parent as correct.
            for (var i = 0; i < e.hierarchy.childCount; i++)
                Walk(sb, e.hierarchy[i], depth + 1, state);
        }

        private static string Describe(VisualElement e)
        {
            var classes = string.Join(".", e.GetClasses());
            var name = string.IsNullOrEmpty(e.name) ? "" : $"#{e.name}";
            return $"<{e.GetType().Name}{name}{(classes.Length == 0 ? "" : "." + classes)}>";
        }

        private static string Report(VisualElement e, string group)
        {
            var s = e.resolvedStyle;
            switch (group)
            {
                case "geometry":
                {
                    var r = e.worldBound;
                    return $" y={F(r.y)} h={F(r.height)} x={F(r.x)} w={F(r.width)}";
                }
                case "box":
                    // Overflow is reported as the INLINE value and labelled as such: it is absent
                    // from IResolvedStyle, so a stylesheet-driven overflow cannot be read here and
                    // must not be implied by printing a bare number.
                    return $" m=({F(s.marginTop)},{F(s.marginRight)},{F(s.marginBottom)},{F(s.marginLeft)})" +
                           $" p=({F(s.paddingTop)},{F(s.paddingRight)},{F(s.paddingBottom)},{F(s.paddingLeft)})" +
                           $" bw=({F(s.borderTopWidth)},{F(s.borderRightWidth)},{F(s.borderBottomWidth)},{F(s.borderLeftWidth)})" +
                           $" overflow(inline:{InlineOverflow(e.style.overflow)})";
                case "display":
                    // Inline alongside resolved: "this element sets it" and "something upstream sets
                    // it" look identical in the resolved value and need completely different fixes.
                    return $" disp={s.display}(inline:{InlineEnum(e.style.display)})" +
                           $" vis={s.visibility} opacity={F(s.opacity)}(inline:{InlineFloat(e.style.opacity)})" +
                           $" enabled={e.enabledSelf}";
                case "colour":
                    return $" bg={Col(s.backgroundColor)}(inline:{InlineColour(e.style.backgroundColor)})" +
                           $" fg={Col(s.color)}(inline:{InlineColour(e.style.color)})" +
                           $" border={Col(s.borderTopColor)}";
                case "text":
                    return TextOf(e);
                case "image":
                {
                    var image = s.backgroundImage.texture;
                    return $" bgImage={(image == null ? "NONE" : $"{image.width}x{image.height}")}" +
                           $" tint={Col(s.unityBackgroundImageTintColor)}";
                }
                default:
                    return "";
            }
        }

        /// <summary>
        /// The element's own text/value, plus its field label where it has one. Foldouts and toggles
        /// carry both a caption and an expansion/checked state, and which of those is wrong is
        /// exactly the sort of thing a bounds-only dump cannot distinguish.
        /// </summary>
        private static string TextOf(VisualElement e)
        {
            var parts = new List<string>();
            switch (e)
            {
                case Foldout foldout:
                    parts.Add($"text='{foldout.text}' expanded={foldout.value}");
                    break;
                case Toggle toggle:
                    parts.Add($"text='{toggle.text}' checked={toggle.value}");
                    break;
                case TextElement textElement:
                    parts.Add($"text='{textElement.text}'");
                    break;
            }

            if (LabelOf(e) is { } label)
                parts.Add($"label='{label}'");
            return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
        }

        private static string LabelOf(VisualElement e)
        {
            var type = e.GetType();
            if (!_labelProperties.TryGetValue(type, out var property))
            {
                // BaseField<T> is generic, so its label cannot be reached by a type pattern; a
                // cached property lookup covers every field type without naming any of them.
                try
                {
                    property = type.GetProperty("label", BindingFlags.Public | BindingFlags.Instance);
                    if (property is null || property.PropertyType != typeof(string) || !property.CanRead)
                        property = null;
                }
                catch (AmbiguousMatchException)
                {
                    property = null;
                }

                _labelProperties[type] = property;
            }

            if (property is null) return null;
            try
            {
                return property.GetValue(e) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static bool? IsLocked(EditorWindow window)
        {
            var type = window.GetType();
            if (!_lockProperties.TryGetValue(type, out var property))
            {
                try
                {
                    property = type.GetProperty("isLocked",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (property is null || property.PropertyType != typeof(bool) || !property.CanRead)
                        property = null;
                }
                catch (AmbiguousMatchException)
                {
                    property = null;
                }

                _lockProperties[type] = property;
            }

            if (property is null) return null;
            try
            {
                return (bool)property.GetValue(window);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The window's EXISTING UI Toolkit root, or null if it has not built one.
        /// <para>
        /// Deliberately not <c>EditorWindow.rootVisualElement</c>: that getter CREATES the root on
        /// first access, and for windows supporting overlays it also initialises the overlay canvas.
        /// Reading it across every open window — which listing does — would therefore build UI in
        /// background tabs that had never drawn, i.e. change editor state from tools that report
        /// themselves read-only, and would make a "does this window have a root" answer always yes.
        /// </para>
        /// </summary>
        private static VisualElement SafeRoot(EditorWindow window)
        {
            var type = window.GetType();
            if (!_rootFields.TryGetValue(type, out var field))
            {
                // Declared on EditorWindow itself, so walk up from the concrete window type.
                for (var t = type; t is not null && field is null; t = t.BaseType)
                    field = t.GetField("m_UIRootElement", BindingFlags.NonPublic | BindingFlags.Instance);
                _rootFields[type] = field;
            }

            if (field is null)
                return null;
            try
            {
                return field.GetValue(window) as VisualElement;
            }
            catch (Exception)
            {
                // A window mid-teardown; absence of a root is the honest answer.
                return null;
            }
        }

        private static string SafeTitle(EditorWindow window)
        {
            try
            {
                return window.titleContent?.text ?? "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);

        // Rounded, never truncated: truncating turns 0.345 into a value that disagrees with the
        // stylesheet it actually came from, and the phantom one-off sends the reader chasing it.
        private static string Col(Color c) =>
            $"#{Mathf.RoundToInt(c.r * 255):X2}{Mathf.RoundToInt(c.g * 255):X2}{Mathf.RoundToInt(c.b * 255):X2}" +
            $"{(c.a < 1f ? $"a{c.a.ToString("F2", CultureInfo.InvariantCulture)}" : "")}";

        private static string InlineColour(StyleColor style) =>
            style.keyword == StyleKeyword.Undefined ? Col(style.value) : style.keyword.ToString();

        private static string InlineFloat(StyleFloat style) =>
            style.keyword == StyleKeyword.Undefined ? F(style.value) : style.keyword.ToString();

        private static string InlineEnum(StyleEnum<DisplayStyle> style) =>
            style.keyword == StyleKeyword.Undefined ? style.value.ToString() : style.keyword.ToString();

        private static string InlineOverflow(StyleEnum<Overflow> style) =>
            style.keyword == StyleKeyword.Undefined ? style.value.ToString() : style.keyword.ToString();
    }
}
