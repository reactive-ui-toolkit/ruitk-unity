using System;
using System.IO;
using System.Linq;
using Xunit;

namespace UitkxLanguageServer.Tests;

/// <summary>
/// Regression coverage for U-34: <c>WorkspaceIndex.s_propPattern</c> used to only
/// match brace-bodied public properties (<c>public string Foo { get; set; }</c>)
/// and silently skipped expression-bodied ones (<c>public string Foo => "bar";</c>),
/// so hover/completion for such props showed no type/doc info.
/// </summary>
public sealed class WorkspaceIndexPropsTests : IDisposable
{
    private readonly string _root;

    public WorkspaceIndexPropsTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "UitkxWorkspaceIndexPropsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteCs(string relPath, string body)
    {
        var full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        return full;
    }

    [Fact]
    public void ExpressionBodiedProp_IsIndexed()
    {
        var idx = new WorkspaceIndex();
        var file = WriteCs("Widget/WidgetProps.cs",
            "public class WidgetProps\n" +
            "{\n" +
            "    public string Label { get; set; } = \"\";\n" +
            "    public string ComputedLabel => Label.ToUpperInvariant();\n" +
            "}\n");

        idx.Refresh(file);

        var props = idx.GetProps("Widget");
        Assert.Contains(props, p => p.Name == "Label");
        Assert.Contains(props, p => p.Name == "ComputedLabel");
    }

    [Fact]
    public void UnderscoreParam_ExposesUnprefixedProp()
    {
        // The leading underscore marks a param deliberately unused WITHOUT
        // renaming its public prop (FunctionParam.PropSourceName). This
        // surface feeds 0109, attribute completion, hover, and the typed
        // attr check — all must see `onCountChanged`, never the underscored name.
        var idx = new WorkspaceIndex();
        var file = WriteCs("Widget/Widget.uitkx",
            "export VirtualNode Widget(Action<int>? _onCountChanged = null, string title = \"\") {\n" +
            "  return (\n    <Label text={title}/>\n  );\n}\n");

        idx.Refresh(file);

        var props = idx.GetProps("Widget");
        Assert.Contains(props, p => p.Name == "onCountChanged");
        Assert.Contains(props, p => p.Name == "title");
        Assert.DoesNotContain(props, p => p.Name == "_onCountChanged");
    }

    [Fact]
    public void ExpressionBodiedProp_WithGenericType_IsIndexed()
    {
        var idx = new WorkspaceIndex();
        var file = WriteCs("Gadget/GadgetProps.cs",
            "public class GadgetProps\n" +
            "{\n" +
            "    public Action<int>? OnClick => _onClick;\n" +
            "}\n");

        idx.Refresh(file);

        var props = idx.GetProps("Gadget");
        var onClick = props.FirstOrDefault(p => p.Name == "OnClick");
        Assert.NotNull(onClick);
        Assert.Equal("Action<int>?", onClick!.Type);
    }
}
