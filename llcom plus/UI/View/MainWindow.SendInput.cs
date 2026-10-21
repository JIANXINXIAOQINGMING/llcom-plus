using llcom_plus.Tools;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace llcom_plus
{
    public partial class MainWindow
    {
        private SendInputHistory sendInputHistory;
        private bool applyingSendHistory;
        private int sendSuggestionCaretIndex;
        private int sendSuggestionSelectionStart;
        private int sendSuggestionSelectionLength;

        private SendInputHistory GetSendInputHistory() => sendInputHistory ??
            (sendInputHistory = new SendInputHistory(Global.setting.GetSendInputHistory()));

        private void RememberSendInput(string text)
        {
            var history = GetSendInputHistory();
            if (!history.Record(text)) return;
            try { Global.setting.SaveSendInputHistory(history.Entries); }
            catch (Exception ex)
            {
                Logger.AddUartLogDebug("[SendInputHistory] Could not save history: " + ex.Message);
            }
        }

        private bool HandleSendInputNavigation(Key key, ModifierKeys modifiers)
        {
            if (modifiers != ModifierKeys.None) return false;
            if (sendSuggestPopup.IsOpen && HandleSendSuggestionKey(key)) return true;
            if (key == Key.Tab)
            {
                UpdateSendSuggestions();
                return true;
            }
            return (key == Key.Up || key == Key.Down) && NavigateSendInputHistory(key == Key.Up);
        }

        private bool NavigateSendInputHistory(bool older)
        {
            var input = toSendDataTextBox;
            if (input.IsReadOnly) return false;
            // Match zsh's up/down-line-or-history: preserve normal line movement,
            // including wrapped display lines, until the top/bottom is reached.
            var line = input.GetLineIndexFromCharacterIndex(input.CaretIndex);
            if (line >= 0)
            {
                if (older ? line > 0 : line < input.LineCount - 1) return false;
            }
            else
            {
                var text = input.Text ?? string.Empty;
                var caret = Math.Min(input.CaretIndex, text.Length);
                var side = older ? text.Substring(0, caret) : text.Substring(caret);
                if (side.IndexOfAny(new[] { '\r', '\n' }) >= 0) return false;
            }

            var current = new SendInputHistory.State(input.Text, input.CaretIndex,
                input.SelectionStart, input.SelectionLength);
            if (!GetSendInputHistory().TryMove(older, current, out var recalled)) return false;
            applyingSendHistory = true;
            try
            {
                input.SetCurrentValue(TextBox.TextProperty, recalled.Text);
                input.Select(recalled.SelectionStart, recalled.SelectionLength);
                if (recalled.SelectionLength == 0) input.CaretIndex = recalled.CaretIndex;
                var caretLine = input.GetLineIndexFromCharacterIndex(input.CaretIndex);
                if (caretLine >= 0) input.ScrollToLine(caretLine);
            }
            finally { applyingSendHistory = false; }
            CloseSendSuggestions();
            return true;
        }

        private void CloseSendSuggestions()
        {
            if (sendSuggestPopup != null) sendSuggestPopup.IsOpen = false;
        }

        private void ToSendDataTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            // A moved caret invalidates the completion's replacement range.
            if (!applyingSendSuggestion &&
                (toSendDataTextBox.CaretIndex != sendSuggestionCaretIndex ||
                 toSendDataTextBox.SelectionStart != sendSuggestionSelectionStart ||
                 toSendDataTextBox.SelectionLength != sendSuggestionSelectionLength))
                CloseSendSuggestions();
        }

        private void SendSuggestListBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleSendInputNavigation(e.Key, Keyboard.Modifiers)) e.Handled = true;
        }
    }
}
