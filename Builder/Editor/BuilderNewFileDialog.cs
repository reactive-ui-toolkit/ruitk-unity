#if UNITY_EDITOR
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder
{
    /// <summary>
    /// New-file creation with VE-D9 name validation: PascalCase identifier,
    /// no collision with a sibling file. Templates are the new-mode idioms;
    /// style/hooks kinds get their filename suffix automatically. The file is
    /// written to disk (creation is inherently a disk operation) and opened
    /// into the current builder tree.
    /// </summary>
    internal sealed class BuilderNewFileDialog : EditorWindow
    {
        private static readonly Regex s_pascal = new Regex("^[A-Z][A-Za-z0-9]*$");

        private string _directory;
        private BuilderWindow _owner;
        private TextField _nameField;
        private DropdownField _kindField;
        private Label _error;

        private string _presetKind;

        public static void Show(string directory, BuilderWindow owner, string presetKind = null)
        {
            var window = CreateInstance<BuilderNewFileDialog>();
            window._directory = directory;
            window._owner = owner;
            window._presetKind = presetKind;
            window.titleContent = new GUIContent("New UITKX File");
            window.minSize = new Vector2(340f, 128f);
            window.maxSize = new Vector2(340f, 128f);
            window.ShowUtility();
        }

        private void CreateGUI()
        {
            rootVisualElement.style.paddingLeft = 8f;
            rootVisualElement.style.paddingRight = 8f;
            rootVisualElement.style.paddingTop = 8f;

            _kindField = new DropdownField(
                "Kind",
                new System.Collections.Generic.List<string> { "Component", "Hooks", "Style", "Utils" },
                0);
            if (!string.IsNullOrEmpty(_presetKind))
                _kindField.value = _presetKind;
            rootVisualElement.Add(_kindField);

            _nameField = new TextField("Name");
            rootVisualElement.Add(_nameField);

            _error = new Label { style = { color = new Color(0.94f, 0.55f, 0.45f), fontSize = 10f } };
            rootVisualElement.Add(_error);

            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 6f },
            };
            row.Add(new Button(Close) { text = "Cancel" });
            row.Add(new Button(Create) { text = "Create" });
            rootVisualElement.Add(row);
        }

        private void Create()
        {
            string name = (_nameField.value ?? "").Trim();
            if (!s_pascal.IsMatch(name))
            {
                _error.text = "Name must be a PascalCase identifier (start uppercase, letters/digits only).";
                return;
            }

            string kind = _kindField.value;
            string fileName = kind switch
            {
                "Hooks" => name + ".hooks.uitkx",
                "Style" => name + ".style.uitkx",
                _ => name + ".uitkx",
            };
            string path = Path.Combine(_directory, fileName);
            if (File.Exists(path))
            {
                _error.text = fileName + " already exists here.";
                return;
            }

            File.WriteAllText(path, TemplateFor(kind, name));
            AssetDatabase.Refresh();
            Close();
            _owner?.OpenAdditionalFile(path);
        }

        private static string TemplateFor(string kind, string name)
        {
            switch (kind)
            {
                case "Style":
                    return "export Style " + name + "Root = new Style {\n  FlexGrow = 1,\n};\n";
                case "Hooks":
                    return "export (int value, Action increment) use" + name
                        + "() {\n  var (value, setValue) = useState(0);\n"
                        + "  return (value, () => setValue(value + 1));\n}\n";
                case "Utils":
                    return "export string " + name + "Text(string input) {\n  return input;\n}\n";
                default:
                    return "export VirtualNode " + name + "() {\n  return (\n    <VisualElement>\n"
                        + "      <Label text=\"" + name + "\" />\n    </VisualElement>\n  );\n}\n";
            }
        }
    }
}
#endif
