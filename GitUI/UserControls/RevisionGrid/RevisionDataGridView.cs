﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitCommands;
using GitExtUtils;
using GitExtUtils.GitUI;
using GitExtUtils.GitUI.Theming;
using GitUI.Theming;
using GitUI.UserControls.RevisionGrid.Columns;
using GitUI.UserControls.RevisionGrid.Graph;
using GitUIPluginInterfaces;

namespace GitUI.UserControls.RevisionGrid
{
    [Flags]
    public enum RevisionNodeFlags
    {
        None = 0,
        CheckedOut = 1,
        HasRef = 2,
        OnlyFirstParent = 4
    }

    public sealed partial class RevisionDataGridView : DataGridView
    {
        private readonly SolidBrush _alternatingRowBackgroundBrush;
        private readonly SolidBrush _authoredHighlightBrush;

        private readonly BackgroundUpdater _backgroundUpdater;
        private readonly Stopwatch _lastRepaint = Stopwatch.StartNew();

        internal RevisionGraph _revisionGraph = new();

        private readonly List<ColumnProvider> _columnProviders = new();

        private int _backgroundScrollTo;
        private int _consecutiveScrollMessageCnt = 0; // Is used to detect if a forced repaint is needed.
        private int _rowHeight; // Height of elements in the cache. Is equal to the control's row height.

        private VisibleRowRange _visibleRowRange;

        private Font _normalFont;
        private Font _boldFont;
        private Font _monospaceFont;

        public bool UpdatingVisibleRows { get; private set; }
        public bool IsBackgroundUpdaterActive => _backgroundUpdater.IsExecuting;

        public RevisionDataGridView()
        {
            InitFonts();

            _backgroundUpdater = new BackgroundUpdater(UpdateVisibleRowRangeInternalAsync, 25);

            InitializeComponent();
            DoubleBuffered = true;

            _alternatingRowBackgroundBrush = new SolidBrush(KnownColor.Window.MakeBackgroundDarkerBy(0.025)); // 0.018
            _authoredHighlightBrush = new SolidBrush(AppColor.AuthoredHighlight.GetThemeColor());

            UpdateRowHeight();

            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            ColumnWidthChanged += (s, e) =>
            {
                if (e.Column.Tag is ColumnProvider provider)
                {
                    provider.OnColumnWidthChanged(e);
                }
            };

            Scroll += (_, _) => UpdateVisibleRowRange();
            Resize += (_, _) => UpdateVisibleRowRange();
            GotFocus += (_, _) => InvalidateSelectedRows();
            LostFocus += (_, _) => InvalidateSelectedRows();
            RowPrePaint += (_, _) => _lastRepaint.Restart();

            CellPainting += OnCellPainting;
            CellFormatting += (_, e) =>
            {
                if (Columns[e.ColumnIndex].Tag is ColumnProvider provider)
                {
                    var revision = GetRevision(e.RowIndex);
                    if (revision is not null)
                    {
                        provider.OnCellFormatting(e, revision);
                    }
                }
            };

            _revisionGraph.Updated += () =>
            {
                // We have to post this since the thread owns a lock on GraphData that we'll
                // need in order to re-draw the graph.
                this.InvokeAsync(() =>
                    {
                        Debug.Assert(_rowHeight != 0, "_rowHeight != 0");

                        // Refresh column providers
                        foreach (var columnProvider in _columnProviders)
                        {
                            columnProvider.Refresh(_rowHeight, _visibleRowRange);
                        }

                        Invalidate();
                    })
                    .FileAndForget();
            };

            VirtualMode = true;
            Clear();

            return;

            void InitializeComponent()
            {
                ((ISupportInitialize)this).BeginInit();
                SuspendLayout();
                AllowUserToAddRows = false;
                AllowUserToDeleteRows = false;
                BackgroundColor = SystemColors.Window;
                CellBorderStyle = DataGridViewCellBorderStyle.None;
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    Alignment = DataGridViewContentAlignment.MiddleLeft,
                    BackColor = SystemColors.Window,
                    ForeColor = SystemColors.ControlText,
                    SelectionBackColor = SystemColors.Highlight,
                    SelectionForeColor = SystemColors.HighlightText,
                    WrapMode = DataGridViewTriState.False
                };
                Dock = DockStyle.Fill;
                GridColor = SystemColors.Window;
                ReadOnly = true;
                RowHeadersVisible = false;
                SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                StandardTab = true;
                ((ISupportInitialize)this).EndInit();
                ResumeLayout(false);
            }

            void InvalidateSelectedRows()
            {
                for (int index = 0; index < SelectedRows.Count; ++index)
                {
                    InvalidateRow(SelectedRows[index].Index);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        internal AuthorRevisionHighlighting? AuthorHighlighting { get; set; }

        // Contains the object Id's that will be selected as soon as all of them have been loaded.
        // The object Id's are in the order in which they were originally selected.
        public IReadOnlyList<ObjectId> ToBeSelectedObjectIds { get; set; } = Array.Empty<ObjectId>();

        private int _loadedToBeSelectedRevisionsCount = 0;

        public bool HasSelection()
        {
            return ToBeSelectedObjectIds.Any() || SelectedRows.Count > 0;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public IReadOnlyList<ObjectId>? SelectedObjectIds
        {
            get
            {
                if (SelectedRows.Count == 0)
                {
                    return null;
                }

                var data = new ObjectId[SelectedRows.Count];

                for (var i = 0; i < SelectedRows.Count; i++)
                {
                    var row = _revisionGraph.GetNodeForRow(SelectedRows[i].Index);

                    if (row is not null && row.GitRevision is not null)
                    {
                        // NOTE returned collection has reverse order of SelectedRows
                        data[SelectedRows.Count - 1 - i] = row.GitRevision.ObjectId;
                    }
                }

                return data;
            }
        }

        internal void AddColumn(ColumnProvider columnProvider)
        {
            _columnProviders.Add(columnProvider);

            columnProvider.Column.Tag = columnProvider;

            Columns.Add(columnProvider.Column);
        }

        private Color GetForeground(DataGridViewElementStates state, int rowIndex)
        {
            bool isNonRelativeGray = AppSettings.RevisionGraphDrawNonRelativesTextGray && !RowIsRelative(rowIndex);
            bool isSelectedAndFocused = state.HasFlag(DataGridViewElementStates.Selected) && Focused;
            return (isNonRelativeGray, isSelectedAndFocused) switch
            {
                (isNonRelativeGray: false, isSelectedAndFocused: false) => SystemColors.ControlText,
                (isNonRelativeGray: false, isSelectedAndFocused: true) => SystemColors.HighlightText,
                (isNonRelativeGray: true, isSelectedAndFocused: false) => SystemColors.GrayText,

                // (isGray: true, isSelected: true)
                _ => getHighlightedGrayTextColor()
            };
        }

        private Color GetCommitBodyForeground(DataGridViewElementStates state, int rowIndex)
        {
            bool isNonRelativeGray = AppSettings.RevisionGraphDrawNonRelativesTextGray && !RowIsRelative(rowIndex);
            bool isSelected = state.HasFlag(DataGridViewElementStates.Selected);

            return (isNonRelativeGray, isSelected) switch
            {
                (isNonRelativeGray: false, isSelected: false) => SystemColors.GrayText,
                (isNonRelativeGray: false, isSelected: true) => getHighlightedGrayTextColor(),
                (isNonRelativeGray: true, isSelected: false) => getGrayTextColor(degreeOfGrayness: 1.4f),

                // (isGray: true, isSelected: true)
                _ => getHighlightedGrayTextColor(degreeOfGrayness: 1.4f)
            };
        }

        private Brush GetBackground(DataGridViewElementStates state, int rowIndex, GitRevision? revision)
        {
            if (state.HasFlag(DataGridViewElementStates.Selected))
            {
                return Focused ? SystemBrushes.Highlight : OtherColors.InactiveSelectionHighlightBrush;
            }

            if (AppSettings.HighlightAuthoredRevisions && revision is not null && !revision.IsArtificial && AuthorHighlighting?.IsHighlighted(revision) != false)
            {
                return _authoredHighlightBrush;
            }

            if (rowIndex % 2 == 0 && AppSettings.RevisionGraphDrawAlternateBackColor)
            {
                return _alternatingRowBackgroundBrush;
            }

            return SystemBrushes.Window;
        }

        private void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            _lastRepaint.Restart();

            Debug.Assert(_rowHeight != 0, "_rowHeight != 0");

            var revision = GetRevision(e.RowIndex);

            if (e.RowIndex < 0 ||
                e.RowIndex >= RowCount ||
                !e.State.HasFlag(DataGridViewElementStates.Visible) ||
                revision is null)
            {
                return;
            }

            Brush backBrush = GetBackground(e.State, e.RowIndex, revision);
            e.Graphics.FillRectangle(backBrush, e.CellBounds);

            if (Columns[e.ColumnIndex].Tag is ColumnProvider provider)
            {
                Color foreColor = GetForeground(e.State, e.RowIndex);
                Color commitBodyForeColor = GetCommitBodyForeground(e.State, e.RowIndex);
                CellStyle cellStyle = new(backBrush, foreColor, commitBodyForeColor, _normalFont, _boldFont, _monospaceFont);

                provider.OnCellPainting(e, revision, _rowHeight, cellStyle);
            }

            e.Handled = true;
        }

        /// <summary>
        /// Add a single revision from the git log to the graph, including segments to parents.
        /// Update visible rows if needed.
        /// </summary>
        /// <param name="revision">The revision to add.</param>
        /// <param name="types">The graph node flags.</param>
        /// <param name="insertAsFirst">Insert the (artificial) revision first in the graph.</param>
        public void Add(GitRevision revision, RevisionNodeFlags types = RevisionNodeFlags.None, bool insertAsFirst = false)
        {
            _revisionGraph.Add(revision, types, insertAsFirst);

            if (ToBeSelectedObjectIds.Contains(revision.ObjectId))
            {
                ++_loadedToBeSelectedRevisionsCount;
            }

            UpdateVisibleRowRange();
        }

        public void Clear()
        {
            _backgroundScrollTo = 0;

            // Set rowcount to 0 first, to ensure it is not possible to select or redraw, since we are about te delete the data
            SetRowCount(0);
            _revisionGraph.Clear();
            _loadedToBeSelectedRevisionsCount = 0;

            // The graphdata is stored in one of the columnproviders, clear this last
            foreach (var columnProvider in _columnProviders)
            {
                columnProvider.Clear();
            }

            // Redraw
            UpdateVisibleRowRange();
            Invalidate(invalidateChildren: true);
        }

        public void LoadingCompleted()
        {
            foreach (ColumnProvider columnProvider in _columnProviders)
            {
                columnProvider.LoadingCompleted();
            }
        }

        /// <summary>
        /// Checks whether the given hash is present in the graph.
        /// </summary>
        /// <param name="objectId">The hash to find.</param>
        /// <returns><see langword="true"/>, if the given hash if found; otherwise <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="objectId"/> is <see langword="null"/>.</exception>
        public bool Contains(ObjectId objectId) => _revisionGraph.Contains(objectId);

        public bool RowIsRelative(int rowIndex)
        {
            return _revisionGraph.IsRowRelative(rowIndex);
        }

        public GitRevision? GetRevision(int rowIndex)
        {
            return _revisionGraph.GetNodeForRow(rowIndex)?.GitRevision;
        }

        private void SetRowCount(int count)
        {
            if (InvokeRequired)
            {
                // DO NOT INVOKE! The RowCount is fixed at other strategic points in time.
                // -Doing this in synch can lock up the application
                // -Doing this async causes the scroll bar to flicker and eats performance
                // -At first I was concerned that returning might lead to some cases where
                //  we have more items in the list than we're showing, but I'm pretty sure
                //  when we're done processing we'll update with the final count, so the
                //  problem will only be temporary, and not able to distinguish it from
                //  just git giving us data slowly.
                ////Invoke(new MethodInvoker(delegate { setRowCount(count); }));
                return;
            }

            UpdatingVisibleRows = true;

            try
            {
                if (CurrentCell is null)
                {
                    RowCount = count;
                    CurrentCell = null;
                }
                else
                {
                    RowCount = count;
                }
            }
            finally
            {
                UpdatingVisibleRows = false;
            }
        }

        private void SelectRowsIfReady(int rowCount)
        {
            // Wait till we have all the row indexes to be selected.
            if (_loadedToBeSelectedRevisionsCount == 0 || _loadedToBeSelectedRevisionsCount < ToBeSelectedObjectIds.Count)
            {
                return;
            }

            foreach (var objectId in ToBeSelectedObjectIds)
            {
                try
                {
                    if (!_revisionGraph.TryGetRowIndex(objectId, out int rowIndexToBeSelected) || rowIndexToBeSelected >= rowCount)
                    {
                        return;
                    }

                    Rows[rowIndexToBeSelected].Selected = true;

                    CurrentCell ??= Rows[rowIndexToBeSelected].Cells[1];
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Not worth crashing for. Ignore exception.
                }
            }

            // The rows to be selected have just been selected. Prevent from selecting them again.
            _loadedToBeSelectedRevisionsCount = 0;
            ToBeSelectedObjectIds = Array.Empty<ObjectId>();
        }

        private void SetRowCountAndSelectRowsIfReady(int rowCount)
        {
            SetRowCount(rowCount);
            SelectRowsIfReady(rowCount);
        }

        private void UpdateVisibleRowRange()
        {
            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime)
            {
                // TODO: Switch to IsDesignMode? See Github discussion in #8809
                // Don't run background operations in the designer.
                return;
            }

            _backgroundUpdater.ScheduleExcecution();
        }

        private async Task UpdateVisibleRowRangeInternalAsync()
        {
            var fromIndex = Math.Max(0, FirstDisplayedScrollingRowIndex);
            var visibleRowCount = _rowHeight > 0 ? (Height / _rowHeight) + 2 /*Add 2 for rounding*/ : 0;

            visibleRowCount = Math.Min(_revisionGraph.Count - fromIndex, visibleRowCount);

            if (_visibleRowRange.FromIndex != fromIndex || _visibleRowRange.Count != visibleRowCount)
            {
                _visibleRowRange = new VisibleRowRange(fromIndex, visibleRowCount);

                if (visibleRowCount > 0)
                {
                    int newBackgroundScrollTo = fromIndex + visibleRowCount;

                    // We always want to set _backgroundScrollTo. Because we want the backgroundthread to stop working when we scroll up
                    if (_backgroundScrollTo != newBackgroundScrollTo)
                    {
                        _backgroundScrollTo = newBackgroundScrollTo;

                        if (AppSettings.ShowRevisionGridGraphColumn)
                        {
                            int scrollTo;
                            int curCount;

                            do
                            {
                                scrollTo = newBackgroundScrollTo;
                                curCount = _revisionGraph.GetCachedCount();
                                await UpdateGraphAsync(fromIndex: curCount, toIndex: scrollTo);
                            }
                            while (curCount < scrollTo);
                        }
                        else
                        {
                            await UpdateGraphAsync(fromIndex: _revisionGraph.Count, toIndex: _revisionGraph.Count);
                        }
                    }

                    await this.InvokeAsync(NotifyProvidersVisibleRowRangeChanged);
                }
            }

            return;

            async Task UpdateGraphAsync(int fromIndex, int toIndex)
            {
                // Cache the next item
                _revisionGraph.CacheTo(currentRowIndex: toIndex, lastToCacheRowIndex: Math.Min(fromIndex + 1500, toIndex));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                int rowCount = _revisionGraph.Count;
                if (RowCount < rowCount)
                {
                    SetRowCountAndSelectRowsIfReady(rowCount);
                }
            }
        }

        private void NotifyProvidersVisibleRowRangeChanged()
        {
            foreach (var provider in _columnProviders)
            {
                provider.OnVisibleRowsChanged(_visibleRowRange);
            }
        }

        public override void Refresh()
        {
            InitFonts();

            UpdateRowHeight();
            UpdateVisibleRowRange();

            // Refresh column providers
            foreach (var columnProvider in _columnProviders)
            {
                columnProvider.Refresh(_rowHeight, _visibleRowRange);
            }

            base.Refresh();
        }

        private void UpdateRowHeight()
        {
            // TODO allow custom grid row spacing
            using var g = Graphics.FromHwnd(Handle);
            _rowHeight = (int)g.MeasureString("By", _normalFont).Height + DpiUtil.Scale(9);
            //// + AppSettings.GridRowSpacing
            RowTemplate.Height = _rowHeight;
        }

        public bool IsRevisionRelative(ObjectId objectId)
        {
            return _revisionGraph.IsRevisionRelative(objectId);
        }

        public GitRevision? GetRevision(ObjectId objectId)
        {
            return _revisionGraph.TryGetNode(objectId, out var node) ? node.GitRevision : null;
        }

        public int? TryGetRevisionIndex(ObjectId? objectId)
        {
            return objectId is not null && _revisionGraph.TryGetRowIndex(objectId, out var index) ? index : null;
        }

        public IReadOnlyList<ObjectId> GetRevisionChildren(ObjectId objectId)
        {
            // We do not need a lock here since we load the data from the first commit and walk through all
            // parents. Children are always loaded, since we start at the newest commit.
            // With lock, loading the commit info slows down terribly.
            if (_revisionGraph.TryGetNode(objectId, out var node))
            {
                var children = node.Children.Select(d => d.GitRevision!.ObjectId).ToList();
                children.Reverse();
                return children;
            }

            return Array.Empty<ObjectId>();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyData)
            {
                case Keys.Home:
                    if (RowCount != 0)
                    {
                        ClearSelection();
                        Rows[0].Selected = true;
                        CurrentCell = Rows[0].Cells[1];
                    }

                    break;
                case Keys.End:
                    if (RowCount != 0)
                    {
                        ClearSelection();
                        Rows[RowCount - 1].Selected = true;
                        CurrentCell = Rows[RowCount - 1].Cells[1];
                    }

                    break;
                case Keys.Control | Keys.C:
                    var selectedRevisions = SelectedObjectIds;
                    if (selectedRevisions is not null && selectedRevisions.Count != 0)
                    {
                        ClipboardUtil.TrySetText(string.Join(Environment.NewLine, selectedRevisions));
                    }

                    break;
                default:
                    base.OnKeyDown(e);
                    break;
            }
        }

        protected override void WndProc(ref Message m)
        {
            ConditionalRepaintInjector(m);
            base.WndProc(ref m);
        }

        /// <summary>
        /// Forces a repaint if the last repaint was more than 50ms ago.
        /// </summary>
        /// <remarks>
        /// In situations where the mouse wheel is spinning fast (for example with free-spinning mouse wheels),
        /// the message pump is flooded with WM_CTLCOLORSCROLLBAR messages and the DataGridView is not repainted.
        /// This method injects a WM_PAINT message in such cases to make the GUI feel more responsive.
        /// </remarks>
        private void ConditionalRepaintInjector(Message m)
        {
            if (m.Msg != NativeMethods.WM_CTLCOLORSCROLLBAR)
            {
                _consecutiveScrollMessageCnt = 0;
                return;
            }

            _consecutiveScrollMessageCnt++;

            if (_consecutiveScrollMessageCnt > 5 && _lastRepaint.ElapsedMilliseconds > 50)
            {
                // inject paint message
                var mm = new Message() { HWnd = Handle, Msg = NativeMethods.WM_PAINT };
                base.WndProc(ref mm);

                _consecutiveScrollMessageCnt = 0;
                _lastRepaint.Restart();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var hit = HitTest(e.X, e.Y);

            if (hit.Type == DataGridViewHitTestType.None)
            {
                // Work around the fact that clicking in the space to the right of the last column does not
                // actually select the row. Instead, we test if the click would hit if done to the far left
                // of the row, and if so, pretend that's what happened.
                const int fakeX = 5;

                hit = HitTest(fakeX, e.Y);

                if (hit.Type == DataGridViewHitTestType.Cell && hit.RowIndex != -1)
                {
                    base.OnMouseDown(new MouseEventArgs(e.Button, e.Clicks, fakeX, e.Y, e.Delta));
                    return;
                }
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (ModifierKeys.HasFlag(Keys.Shift))
            {
                int currentIndex = HorizontalScrollingOffset;
                int scrollLines = DpiUtil.Scale(32);

                HorizontalScrollingOffset = e.Delta switch
                {
                    > 0 => Math.Max(0, currentIndex - scrollLines),
                    < 0 => currentIndex + scrollLines,
                    _ => HorizontalScrollingOffset
                };
            }
            else
            {
                base.OnMouseWheel(e);
            }
        }

        private void InitFonts()
        {
            _normalFont = AppSettings.Font;
            _boldFont = new Font(_normalFont, FontStyle.Bold);
            _monospaceFont = AppSettings.MonospaceFont;
        }

        private static Color getHighlightedGrayTextColor(float degreeOfGrayness = 1f) =>
            ColorHelper.GetHighlightGrayTextColor(
                backgroundColorName: KnownColor.Control,
                textColorName: KnownColor.ControlText,
                highlightColorName: KnownColor.Highlight,
                degreeOfGrayness);

        private static Color getGrayTextColor(float degreeOfGrayness = 1f) =>
            ColorHelper.GetGrayTextColor(textColorName: KnownColor.ControlText, degreeOfGrayness);
    }
}
