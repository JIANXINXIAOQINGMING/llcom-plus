using llcom_plus.Model;
using llcom_plus.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace llcom_plus
{
    public partial class MainWindow
    {
        private const string QuickReorderFormat = "llcom-plus/private-quick-command-reorder";
        private long quickReorderGeneration;
        private Button quickReorderHandle;
        private Point quickReorderStart;
        private QuickReorderSession quickReorderSession;
        private DispatcherTimer quickReorderScrollTimer;
        private Point quickReorderPointer;
        private bool quickReorderInside;
        private bool quickReorderNativeDrag;
        private ToSendData quickActionsItem;
        private Button quickActionsAnchor;
        private int quickActionsPage;
        private long quickActionsGeneration;

        private void OpenQuickCommandActions(ToSendData item, Button anchor)
        {
            if (windowIsClosing || item == null || anchor == null || !toSendListItems.Contains(item) || !QuickSendTab.IsSelected) return;
            if (QuickSendItemSettingsPopup.IsOpen && !QuickSendItemSettingsEditor.CommitWorkflowFields()) return;
            CloseQuickSendItemSettings();
            CloseQuickCommandActions();
            quickActionsItem = item;
            quickActionsAnchor = anchor;
            quickActionsPage = Global.setting.quickSendSelect;
            quickActionsGeneration = quickReorderGeneration;
            QuickCommandActionsMenu.SetCanCopy(toSendListItems.Count < MaxQuickSendItemsPerPage);
            QuickCommandActionsPopup.PlacementTarget = anchor;
            var menuCenter = QuickCommandActionsMenu.AnchorCenter;
            QuickCommandActionsPopup.HorizontalOffset = -anchor.ActualWidth / 2 - menuCenter.X;
            QuickCommandActionsPopup.VerticalOffset = anchor.ActualHeight / 2 - menuCenter.Y;
            QuickCommandActionsPopup.IsOpen = true;
        }

        private bool IsQuickCommandActionCurrent() => quickActionsItem != null && !windowIsClosing &&
            QuickSendTab.IsSelected && quickActionsPage == Global.setting.quickSendSelect &&
            quickActionsGeneration == quickReorderGeneration && toSendListItems.Contains(quickActionsItem) &&
            ReferenceEquals(quickActionsAnchor?.Tag, quickActionsItem);

        private void QuickSendList_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0 || e.HorizontalChange != 0 || e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
                CloseQuickCommandActions();
        }

        private void CloseQuickCommandActions(bool restoreFocus = false)
        {
            var anchor = quickActionsAnchor;
            if (QuickCommandActionsPopup != null) QuickCommandActionsPopup.IsOpen = false;
            quickActionsItem = null;
            quickActionsAnchor = null;
            if (restoreFocus && anchor?.IsVisible == true && !windowIsClosing) anchor.Focus();
        }

        private void QuickCommandActionsPopup_Closed(object sender, EventArgs e)
        {
            quickActionsItem = null;
            quickActionsAnchor = null;
        }

        private void QuickCommandActions_Copy(object sender, EventArgs e)
        {
            var item = IsQuickCommandActionCurrent() ? quickActionsItem : null;
            CloseQuickCommandActions();
            if (item != null) DuplicateQuickCommand(item);
        }

        private void QuickCommandActions_Delete(object sender, EventArgs e)
        {
            var item = IsQuickCommandActionCurrent() ? quickActionsItem : null;
            CloseQuickCommandActions();
            if (item != null) RemoveQuickSendItem(item);
        }

        private void QuickCommandActions_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            CloseQuickCommandActions(restoreFocus: true);
            e.Handled = true;
        }

        // Private, non-serializable identity: no file/text drops, other windows,
        // or stale page contents can impersonate a local reorder operation.
        private sealed class QuickReorderSession
        {
            internal ToSendData Item;
            internal ToSendData[] Rows;
            internal long Generation;
            internal int Page;
        }

        private void InvalidateQuickReorder()
        {
            CloseQuickCommandActions();
            quickReorderGeneration++;
            ResetQuickReorder();
        }

        private void ResetQuickReorder()
        {
            quickReorderSession = null;
            quickReorderInside = false;
            quickReorderScrollTimer?.Stop();
            quickReorderScrollTimer = null;
            if (QuickSendInsertionLine != null) QuickSendInsertionLine.Visibility = Visibility.Collapsed;
            var handle = quickReorderHandle;
            quickReorderHandle = null;
            if (handle?.IsMouseCaptured == true) handle.ReleaseMouseCapture();
        }

        private QuickReorderSession BeginQuickReorder(ToSendData item)
        {
            CloseQuickCommandActions();
            if (item == null || !toSendListItems.Contains(item) || windowIsClosing) return null;
            return quickReorderSession = new QuickReorderSession
            {
                Item = item, Rows = toSendListItems.ToArray(),
                Generation = quickReorderGeneration, Page = Global.setting.quickSendSelect
            };
        }

        private bool IsQuickReorderCurrent(QuickReorderSession session)
        {
            return session != null && ReferenceEquals(session, quickReorderSession) && !windowIsClosing &&
                session.Generation == quickReorderGeneration && session.Page == Global.setting.quickSendSelect &&
                session.Rows.Length == toSendListItems.Count &&
                session.Rows.SequenceEqual(toSendListItems);
        }

        private bool CommitQuickReorder(QuickReorderSession session, int insertionIndex)
        {
            if (!IsQuickReorderCurrent(session) || insertionIndex < 0 || insertionIndex > toSendListItems.Count) return false;
            var oldIndex = toSendListItems.IndexOf(session.Item);
            var newIndex = insertionIndex > oldIndex ? insertionIndex - 1 : insertionIndex;
            if (oldIndex < 0 || oldIndex == newIndex) return false;
            ExitQuickSendKeyboardNavigation();
            CloseQuickSendItemSettings();
            // Move the model itself, never reconstruct a command or any of its
            // script/response settings. Save uses the existing snapshot debounce.
            toSendListItems.Move(oldIndex, newIndex);
            FinishQuickCommandEdit(session.Item);
            FocusQuickReorderHandle(session.Item, session.Generation);
            return true;
        }

        private void FocusQuickReorderHandle(ToSendData item, long generation)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (windowIsClosing || generation != quickReorderGeneration || !toSendListItems.Contains(item)) return;
                toSendList.UpdateLayout();
                var container = toSendList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
                (container?.Template.FindName("QuickSendRowDragHandle", container) as Button)?.Focus();
            }), DispatcherPriority.Input);
        }

        private void QuickSendDragHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is Button handle) || !(handle.Tag is ToSendData item)) return;
            ResetQuickReorder();
            if (QuickSendItemSettingsPopup.IsOpen && !QuickSendItemSettingsEditor.CommitWorkflowFields()) return;
            if (BeginQuickReorder(item) == null) return;
            ExitQuickSendKeyboardNavigation();
            CloseQuickSendItemSettings();
            quickReorderHandle = handle;
            quickReorderStart = e.GetPosition(toSendList);
            handle.Focus();
            handle.CaptureMouse();
            e.Handled = true;
        }

        private void QuickSendDragHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!ReferenceEquals(sender, quickReorderHandle) || quickReorderSession == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) { ResetQuickReorder(); return; }
            var point = e.GetPosition(toSendList);
            if (Math.Abs(point.X - quickReorderStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(point.Y - quickReorderStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var handle = quickReorderHandle;
            var session = quickReorderSession;
            quickReorderHandle = null;
            handle.ReleaseMouseCapture();
            var previousOpacity = handle.Opacity;
            try
            {
                handle.SetCurrentValue(OpacityProperty, 0.5);
                quickReorderScrollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(70) };
                quickReorderScrollTimer.Tick += QuickReorderScrollTick;
                quickReorderScrollTimer.Start();
                quickReorderNativeDrag = true;
                // The live model stays private. Never deserialize external drag data.
                DragDrop.DoDragDrop(handle, new DataObject(QuickReorderFormat, "move"), DragDropEffects.Move);
            }
            finally
            {
                quickReorderNativeDrag = false;
                handle.SetCurrentValue(OpacityProperty, previousOpacity);
                ResetQuickReorder();
            }
            e.Handled = true;
        }

        private void QuickSendDragHandle_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!ReferenceEquals(sender, quickReorderHandle)) return;
            var handle = quickReorderHandle;
            var session = quickReorderSession;
            var point = e.GetPosition(handle);
            var open = IsQuickReorderCurrent(session) && new Rect(handle.RenderSize).Contains(point);
            ResetQuickReorder();
            if (open) OpenQuickCommandActions(session.Item, handle);
            e.Handled = true;
        }

        private void QuickSendDragHandle_LostCapture(object sender, MouseEventArgs e)
        {
            if (ReferenceEquals(sender, quickReorderHandle)) ResetQuickReorder();
        }

        private void QuickSendDragHandle_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed || !IsQuickReorderCurrent(quickReorderSession) || !QuickSendTab.IsSelected)
            { e.Action = DragAction.Cancel; e.Handled = true; }
        }

        private void QuickSendDragHandle_KeyDown(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { ResetQuickReorder(); CloseQuickCommandActions(); e.Handled = true; return; }
            if ((key == Key.Enter || key == Key.Space || key == Key.Apps) &&
                sender is Button actionHandle && actionHandle.Tag is ToSendData actionItem)
            {
                ResetQuickReorder();
                OpenQuickCommandActions(actionItem, actionHandle);
                if (QuickCommandActionsPopup.IsOpen) QuickCommandActionsMenu.FocusFirstAction();
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers != ModifierKeys.Alt || key != Key.Up && key != Key.Down ||
                !(sender is Button handle) || !(handle.Tag is ToSendData item)) return;
            ResetQuickReorder();
            if (QuickSendItemSettingsPopup.IsOpen && !QuickSendItemSettingsEditor.CommitWorkflowFields())
            { e.Handled = true; return; }
            var session = BeginQuickReorder(item);
            var index = toSendListItems.IndexOf(item);
            CommitQuickReorder(session, key == Key.Up ? index - 1 : index + 2);
            ResetQuickReorder();
            e.Handled = true;
        }

        private QuickReorderSession GetQuickReorderSession(IDataObject data)
        {
            if (!quickReorderNativeDrag || !QuickSendTab.IsSelected || !IsQuickReorderCurrent(quickReorderSession))
                return null;
            try
            {
                return data != null && data.GetDataPresent(QuickReorderFormat, false) ? quickReorderSession : null;
            }
            catch (Exception) { return null; } // Untrusted external drag data.
        }

        private void QuickSendList_Unloaded(object sender, RoutedEventArgs e) => InvalidateQuickReorder();

        private void QuickSendList_DragOver(object sender, DragEventArgs e)
        {
            var session = GetQuickReorderSession(e.Data);
            quickReorderPointer = e.GetPosition(toSendList);
            quickReorderInside = session != null && QuickReorderViewport().Contains(quickReorderPointer);
            e.Effects = quickReorderInside ? DragDropEffects.Move : DragDropEffects.None;
            ShowQuickReorderInsertion(quickReorderInside ? quickReorderPointer : new Point(-1, -1));
            // Prevent TextBox's own drop handler from inserting drag payloads.
            e.Handled = true;
        }

        private void QuickSendList_DragLeave(object sender, DragEventArgs e)
        {
            if (!QuickReorderViewport().Contains(e.GetPosition(toSendList)))
            {
                quickReorderInside = false;
                QuickSendInsertionLine.Visibility = Visibility.Collapsed;
            }
            e.Handled = true;
        }

        private void QuickSendList_Drop(object sender, DragEventArgs e)
        {
            var session = GetQuickReorderSession(e.Data);
            var position = e.GetPosition(toSendList);
            var index = QuickReorderViewport().Contains(position) ? QuickReorderInsertionAt(position, out _) : -1;
            e.Effects = CommitQuickReorder(session, index) ? DragDropEffects.Move : DragDropEffects.None;
            ResetQuickReorder();
            e.Handled = true;
        }

        private Rect QuickReorderViewport()
        {
            var presenter = QuickReorderDescendants<ScrollContentPresenter>(toSendList).FirstOrDefault();
            return presenter == null ? new Rect(0, 0, toSendList.ActualWidth, toSendList.ActualHeight) :
                presenter.TransformToAncestor(toSendList).TransformBounds(new Rect(presenter.RenderSize));
        }

        private static IEnumerable<T> QuickReorderDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                else foreach (var descendant in QuickReorderDescendants<T>(child)) yield return descendant;
            }
        }

        private int QuickReorderInsertionAt(Point point, out double lineY)
        {
            lineY = 0;
            var viewport = QuickReorderViewport();
            if (!viewport.Contains(point)) return -1;
            var result = -1;
            // Walk realized containers only; do not enumerate all 10,000 rows
            // or disable virtualization while the user drags near an edge.
            foreach (var row in QuickReorderDescendants<ListBoxItem>(toSendList))
            {
                var index = toSendList.ItemContainerGenerator.IndexFromContainer(row);
                if (index < 0) continue;
                var bounds = row.TransformToAncestor(toSendList).TransformBounds(new Rect(row.RenderSize));
                if (bounds.Bottom <= viewport.Top || bounds.Top >= viewport.Bottom) continue;
                if (point.Y < bounds.Top + bounds.Height / 2)
                { lineY = Math.Max(viewport.Top, bounds.Top); return index; }
                lineY = Math.Min(viewport.Bottom - 3, bounds.Bottom);
                result = index + 1;
            }
            return result;
        }

        private void ShowQuickReorderInsertion(Point point)
        {
            var index = QuickReorderInsertionAt(point, out var lineY);
            var sourceIndex = quickReorderSession == null ? -1 : toSendListItems.IndexOf(quickReorderSession.Item);
            if (index < 0 || sourceIndex < 0 || index == sourceIndex || index == sourceIndex + 1)
            { QuickSendInsertionLine.Visibility = Visibility.Collapsed; return; }
            var viewport = QuickReorderViewport();
            var position = toSendList.TranslatePoint(new Point(viewport.Left + 2, lineY), QuickSendDragOverlay);
            QuickSendInsertionLine.Width = Math.Max(0, viewport.Width - 4);
            Canvas.SetLeft(QuickSendInsertionLine, position.X);
            Canvas.SetTop(QuickSendInsertionLine, position.Y);
            QuickSendInsertionLine.Visibility = Visibility.Visible;
        }

        private void QuickReorderScrollTick(object sender, EventArgs e)
        {
            if (!quickReorderInside || !IsQuickReorderCurrent(quickReorderSession)) return;
            var viewport = QuickReorderViewport();
            var scroll = QuickReorderDescendants<ScrollViewer>(toSendList).FirstOrDefault();
            if (scroll == null) return;
            if (quickReorderPointer.Y < viewport.Top + 28) scroll.LineUp();
            else if (quickReorderPointer.Y > viewport.Bottom - 28) scroll.LineDown();
            else return;
            toSendList.UpdateLayout();
            ShowQuickReorderInsertion(quickReorderPointer);
        }
    }
}
