using System;
using Ruitk.Builder;

/// <summary>
/// The prop gestures (add / rename / remove) are text surgery on buffers the
/// builder holds in memory. Every check here runs OUTSIDE Unity against the
/// shipped source, which is the point: the failures worth catching are the
/// parsing ones - a generic argument list split on its comma, a lambda default
/// mistaken for an assignment, an attribute value with a brace inside it - and
/// none of them need an editor to reproduce.
/// </summary>
static class SignatureChecks
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("Signature editing");

        ParseChecks(check);
        AddChecks(check);
        RenameRemoveChecks(check);
        AttributeChecks(check);
        CallSiteChecks(check);
        BodyUseChecks(check);
    }

    static void BodyUseChecks(Action<bool, string> check)
    {
        const string card =
            "export VirtualNode Card(string label) {\n"
            + "  var upper = label.ToUpper();\n"
            + "  return (\n"
            + "    <Label text={label} tooltip={upper} />\n"
            + "  );\n}\n";

        string renamed = BuilderSignatureEdit.RenameParamUses(card, "Card", "label", "title");
        check(renamed.Contains("var upper = title.ToUpper();"),
              "a use in setup code renames with the parameter");
        check(renamed.Contains("text={title}"), "and so does a use inside markup");

        const string attr =
            "export VirtualNode Card(string label) {\n"
            + "  return (<Other label=\"literal\" text={label} />);\n}\n";
        string safe = BuilderSignatureEdit.RenameParamUses(attr, "Card", "label", "title");
        check(safe.Contains("<Other label=\"literal\""),
              "an ATTRIBUTE NAME that matches the parameter is not a use of it");
        check(safe.Contains("text={title}"), "while the real use beside it still renames");

        const string member =
            "export VirtualNode Card(string label) {\n"
            + "  return (<Label text={state.label} />);\n}\n";
        check(BuilderSignatureEdit.RenameParamUses(member, "Card", "label", "title") == member,
              "a MEMBER of the same name is not the parameter");

        const string strLit =
            "export VirtualNode Card(string label) {\n"
            + "  return (<Label text=\"label\" tooltip={label} />);\n}\n";
        string keptLiteral = BuilderSignatureEdit.RenameParamUses(strLit, "Card", "label", "title");
        check(keptLiteral.Contains("text=\"label\""),
              "the same word inside a string literal is left alone");
        check(keptLiteral.Contains("tooltip={title}"), "and the use beside it renames");

        const string twoExports =
            "export VirtualNode A(string label) {\n  return (<Label text={label} />);\n}\n"
            + "export VirtualNode B() {\n  var label = \"own\";\n  return (<Label text={label} />);\n}\n";
        string scoped = BuilderSignatureEdit.RenameParamUses(twoExports, "A", "label", "title");
        check(scoped.Contains("A(string label) {\n  return (<Label text={title} />);"),
              "the rename reaches the whole of its own export's body");
        check(scoped.Contains("var label = \"own\";"),
              "and stops at the export that declares it - a sibling's local is untouched");

        check(BuilderSignatureEdit.CountParamUses(card, "Card", "label") == 2,
              "the uses of a parameter are counted before it is removed");
        check(BuilderSignatureEdit.CountParamUses(
                  "export VirtualNode C(int n) {\n  return (<Label />);\n}\n", "C", "n") == 0,
              "an unused parameter counts zero, so removing it says nothing alarming");
    }

    static void CallSiteChecks(Action<bool, string> check)
    {
        const string buffer =
            "export VirtualNode Screen() {\n"
            + "  return (\n"
            + "    <VisualElement>\n"
            + "      <Card label=\"one\" count={1} />\n"
            + "      <Card label=\"two\" count={2} />\n"
            + "      <Other label=\"three\" />\n"
            + "    </VisualElement>\n"
            + "  );\n}\n";

        string renamed = BuilderSignatureEdit.RewriteCallSites(
            buffer, "Card", tag => BuilderSignatureEdit.RenameAttribute(tag, "label", "title"));
        check(renamed.Contains("<Card title=\"one\"") && renamed.Contains("<Card title=\"two\""),
              "every call site of the component is rewritten in one pass");
        check(renamed.Contains("<Other label=\"three\" />"),
              "a different component that happens to share the prop name is left alone");

        string removed = BuilderSignatureEdit.RewriteCallSites(
            buffer, "Card", tag => BuilderSignatureEdit.RemoveAttribute(tag, "count"));
        check(removed.Contains("<Card label=\"one\" />") && removed.Contains("<Card label=\"two\" />"),
              "the prop is stripped from every call site");

        const string prefixed =
            "export VirtualNode S() {\n  return (<CardHeader label=\"x\" />);\n}\n";
        check(BuilderSignatureEdit.RewriteCallSites(
                  prefixed, "Card",
                  tag => BuilderSignatureEdit.RenameAttribute(tag, "label", "title")) == prefixed,
              "a tag whose name merely STARTS WITH the component's is not a call site");

        const string inString =
            "export VirtualNode S() {\n"
            + "  var seed = \"<Card label=\\\"x\\\" />\";\n"
            + "  return (<Card label=\"y\" />);\n}\n";
        string strTouched = BuilderSignatureEdit.RewriteCallSites(
            inString, "Card", tag => BuilderSignatureEdit.RenameAttribute(tag, "label", "title"));
        check(strTouched.Contains("\"<Card label=\\\"x\\\" />\""),
              "markup inside a C# string literal is not a call site");
        check(strTouched.Contains("(<Card title=\"y\" />)"),
              "and the real call site beside it still rewrites");

        const string multiline =
            "export VirtualNode S() {\n"
            + "  return (\n    <Card\n      label=\"x\"\n      count={2}\n    />\n  );\n}\n";
        string wide = BuilderSignatureEdit.RewriteCallSites(
            multiline, "Card", tag => BuilderSignatureEdit.RemoveAttribute(tag, "count"));
        check(wide.Contains("    <Card\n      label=\"x\"\n    />"),
              "a call site broken over lines loses only the attribute's own line");

        const string none = "export VirtualNode S() {\n  return (<Label text=\"x\" />);\n}\n";
        check(BuilderSignatureEdit.RewriteCallSites(
                  none, "Card", tag => BuilderSignatureEdit.RemoveAttribute(tag, "count")) == none,
              "a file with no call sites is returned unchanged");
    }

    static void ParseChecks(Action<bool, string> check)
    {
        const string simple =
            "export VirtualNode Card(string label, int count = 3) {\n  return (<Label />);\n}\n";

        var ps = BuilderSignatureEdit.Parse(simple, "Card");
        check(ps.Count == 2, "reads both parameters");
        check(ps.Count == 2 && ps[0].Name == "label" && ps[0].Type == "string",
              "reads the type and name");
        check(ps.Count == 2 && ps[0].IsRequired, "no written default means required");
        check(ps.Count == 2 && !ps[1].IsRequired && ps[1].Default == "3",
              "a written default means optional, and is kept verbatim");

        const string generic =
            "export VirtualNode Grid(Dictionary<string, int> cells, int rows) {\n  return (<Label />);\n}\n";
        var g = BuilderSignatureEdit.Parse(generic, "Grid");
        check(g.Count == 2, "a generic argument list is not split on its own comma");
        check(g.Count == 2 && g[0].Type == "Dictionary<string, int>" && g[0].Name == "cells",
              "the generic type survives intact");

        const string lambda =
            "export VirtualNode Btn(Action<int> onPick = i => { }, string label = \"go\") {\n"
            + "  return (<Label />);\n}\n";
        var l = BuilderSignatureEdit.Parse(lambda, "Btn");
        check(l.Count == 2, "a lambda default does not end the parameter list");
        check(l.Count == 2 && l[0].Default == "i => { }",
              "the lambda arrow is not read as the default's '='");
        check(l.Count == 2 && l[1].Default == "\"go\"", "a string default is kept with its quotes");

        const string underscore = "export VirtualNode C(int _count) {\n  return (<Label />);\n}\n";
        var u = BuilderSignatureEdit.Parse(underscore, "C");
        check(u.Count == 1 && u[0].PropName == "count",
              "the unused marker does not rename the prop");

        const string none = "export VirtualNode Empty() {\n  return (<Label />);\n}\n";
        check(BuilderSignatureEdit.Parse(none, "Empty").Count == 0,
              "a parameterless export reads as no props");
        check(BuilderSignatureEdit.Parse(none, "Nope").Count == 0,
              "an export that is not there reads as no props");

        const string multiline =
            "export VirtualNode Wide(\n  string label,\n  int count = 3\n) {\n  return (<Label />);\n}\n";
        var m = BuilderSignatureEdit.Parse(multiline, "Wide");
        check(m.Count == 2 && m[0].Name == "label" && m[1].Name == "count",
              "a parameter list broken over lines reads the same");
    }

    static void AddChecks(Action<bool, string> check)
    {
        const string one = "export VirtualNode Card(string label) {\n  return (<Label />);\n}\n";

        string added = BuilderSignatureEdit.AddParam(one, "Card", "int", "count", "3");
        check(added.Contains("(string label, int count = 3)"), "an optional prop is appended");

        string required = BuilderSignatureEdit.AddParam(
            "export VirtualNode Card(string label = \"hi\") {\n  return (<Label />);\n}\n",
            "Card", "int", "count", null);
        check(required.Contains("(int count, string label = \"hi\")"),
              "a required prop lands BEFORE the optional ones - C# rejects the other order");

        string dup = BuilderSignatureEdit.AddParam(one, "Card", "int", "label", null);
        check(dup == one, "a name that is already declared changes nothing");

        string blank = BuilderSignatureEdit.AddParam(one, "Card", "", "count", null);
        check(blank == one, "a prop with no type changes nothing");

        string toEmpty = BuilderSignatureEdit.AddParam(
            "export VirtualNode E() {\n  return (<Label />);\n}\n", "E", "string", "label", null);
        check(toEmpty.Contains("E(string label)"), "the first prop of a parameterless export");

        const string multiline =
            "export VirtualNode Wide(\n  string label,\n  int count = 3\n) {\n  return (<Label />);\n}\n";
        string wide = BuilderSignatureEdit.AddParam(multiline, "Wide", "bool", "dim", "false");
        check(wide.Contains("\n  bool dim = false\n)"),
              "a list already broken over lines stays broken over lines");
        check(wide.Contains("  string label,\n"), "and the parameters before it keep their shape");
    }

    static void RenameRemoveChecks(Action<bool, string> check)
    {
        const string two =
            "export VirtualNode Card(string label, int count = 3) {\n  return (<Label />);\n}\n";

        string renamed = BuilderSignatureEdit.RenameParam(two, "Card", "label", "title");
        check(renamed.Contains("(string title, int count = 3)"), "a prop renames in place");
        check(renamed.Contains("int count = 3"), "and the default of its neighbour is untouched");

        string missing = BuilderSignatureEdit.RenameParam(two, "Card", "nope", "title");
        check(missing == two, "renaming a prop that is not there changes nothing");

        string removed = BuilderSignatureEdit.RemoveParam(two, "Card", "label");
        check(removed.Contains("(int count = 3)"), "a prop is removed with its type");

        string last = BuilderSignatureEdit.RemoveParam(
            "export VirtualNode C(string label) {\n  return (<Label />);\n}\n", "C", "label");
        check(last.Contains("C()"), "removing the only prop leaves an empty list");

        string gone = BuilderSignatureEdit.RemoveParam(two, "Card", "nope");
        check(gone == two, "removing a prop that is not there changes nothing");
    }

    static void AttributeChecks(Action<bool, string> check)
    {
        const string tag = "<Card label=\"hi\" count={state.N} />";

        check(BuilderSignatureEdit.RenameAttribute(tag, "label", "title")
              == "<Card title=\"hi\" count={state.N} />",
              "an attribute renames without touching its value");

        check(BuilderSignatureEdit.RemoveAttribute(tag, "count")
              == "<Card label=\"hi\" />",
              "an attribute is removed with its value and its leading space");

        check(BuilderSignatureEdit.RemoveAttribute(tag, "nope") == tag,
              "removing an attribute that is not there changes nothing");

        const string nested = "<Card style={new Style { Color = Hex(\"#fff\") }} label=\"hi\" />";
        check(BuilderSignatureEdit.RemoveAttribute(nested, "style")
              == "<Card label=\"hi\" />",
              "a braced value with braces inside it is removed whole");

        const string prefix = "<Card onLabelPick={f} label=\"hi\" />";
        check(BuilderSignatureEdit.RemoveAttribute(prefix, "label")
              == "<Card onLabelPick={f} />",
              "an attribute whose name is a substring of another is not confused for it");

        const string wrapped = "<Card\n  label=\"hi\"\n  count={2}\n/>";
        string cut = BuilderSignatureEdit.RemoveAttribute(wrapped, "label");
        check(cut == "<Card\n  count={2}\n/>",
              "removing an attribute on its own line takes the line with it");

        const string quoted = "<Card label=\"a } b\" count={1} />";
        check(BuilderSignatureEdit.RemoveAttribute(quoted, "label")
              == "<Card count={1} />",
              "a brace inside a string value does not end the value early");
    }
}
