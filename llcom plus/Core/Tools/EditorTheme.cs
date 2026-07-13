using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System;
using System.Windows;
using System.Windows.Media;
using System.Xml;

namespace llcom_plus.Tools
{
    internal static class EditorTheme
    {
        private static readonly object DefinitionLock = new object();
        private static IHighlightingDefinition darkJavaScript;

        internal static void Apply(TextEditor editor)
        {
            if (editor == null)
                return;

            editor.SyntaxHighlighting = Global.IsDarkTheme
                ? GetDarkJavaScriptDefinition()
                : HighlightingManager.Instance.GetDefinition("JavaScript");

            var app = Application.Current;
            var accent = app?.TryFindResource("AppAccentBrush") as Brush ?? Brushes.DodgerBlue;
            var selection = app?.TryFindResource("AppAccentSoftBrush") as Brush ?? Brushes.LightBlue;
            editor.TextArea.Caret.CaretBrush = accent;
            editor.TextArea.SelectionBrush = selection;
            editor.TextArea.TextView.CurrentLineBackground = selection;
        }

        private static IHighlightingDefinition GetDarkJavaScriptDefinition()
        {
            if (darkJavaScript != null)
                return darkJavaScript;

            lock (DefinitionLock)
            {
                if (darkJavaScript != null)
                    return darkJavaScript;

                try
                {
                    var resource = Application.GetResourceStream(
                        new Uri("pack://application:,,,/Resources/Themes/JavaScript.Dark.xshd", UriKind.Absolute));
                    if (resource != null)
                    {
                        using (resource.Stream)
                        using (var reader = XmlReader.Create(resource.Stream))
                            darkJavaScript = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    }
                }
                catch
                {
                    // Plain themed text remains readable if the optional definition cannot load.
                }

                return darkJavaScript;
            }
        }
    }
}
