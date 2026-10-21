using System;
using System.Collections.Generic;

namespace llcom_plus.Tools
{
    // Entries are kept oldest first. Navigation owns a temporary draft, never
    // edits a saved entry, and returns whole inputs including embedded newlines.
    internal sealed class SendInputHistory
    {
        internal const int MaxEntries = 200;
        internal const int MaxEntryCharacters = 16384;
        internal const int MaxCharacters = 262144;

        private readonly List<string> entries = new List<string>();
        private int characterCount;
        private int navigationIndex = -1;
        private State draft;

        internal SendInputHistory(IEnumerable<string> entries = null)
        {
            if (entries == null) return;
            foreach (var entry in entries) Record(entry);
        }

        internal IReadOnlyList<string> Entries => entries.ToArray();
        internal int Count => entries.Count;

        internal bool Record(string text)
        {
            ResetNavigation();
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxEntryCharacters ||
                (entries.Count > 0 && string.Equals(entries[entries.Count - 1], text, StringComparison.Ordinal)))
                return false;

            entries.Add(text);
            characterCount += text.Length;
            while (entries.Count > MaxEntries || characterCount > MaxCharacters)
            {
                characterCount -= entries[0].Length;
                entries.RemoveAt(0);
            }
            return true;
        }

        internal void ResetNavigation()
        {
            navigationIndex = -1;
            draft = null;
        }

        internal bool TryMove(bool older, State current, out State recalled)
        {
            recalled = null;
            if (entries.Count == 0) return false;

            if (navigationIndex < 0)
            {
                if (!older) return false;
                current = current ?? new State(string.Empty, 0, 0, 0);
                var index = entries.Count - 1;
                // Sending keeps the input visible. An initial Up should recall
                // the preceding command rather than appear to do nothing.
                if (index > 0 && string.Equals(current.Text, entries[index], StringComparison.Ordinal)) index--;
                draft = current;
                navigationIndex = index;
            }
            else if (older)
            {
                if (navigationIndex == 0) return false;
                navigationIndex--;
            }
            else
            {
                if (navigationIndex == entries.Count - 1)
                {
                    recalled = draft;
                    ResetNavigation();
                    return true;
                }
                navigationIndex++;
            }

            var text = entries[navigationIndex];
            recalled = new State(text, text.Length, text.Length, 0);
            return true;
        }

        internal sealed class State
        {
            internal State(string text, int caretIndex, int selectionStart, int selectionLength)
            {
                Text = text ?? string.Empty;
                CaretIndex = Clamp(caretIndex, Text.Length);
                SelectionStart = Clamp(selectionStart, Text.Length);
                SelectionLength = Clamp(selectionLength, Text.Length - SelectionStart);
            }

            internal string Text { get; }
            internal int CaretIndex { get; }
            internal int SelectionStart { get; }
            internal int SelectionLength { get; }

            private static int Clamp(int value, int maximum) => Math.Max(0, Math.Min(value, maximum));
        }
    }
}
