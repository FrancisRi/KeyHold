using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace KeyHoldMacro
{
    internal sealed class MacroEditor : FluentWindow
    {
        private readonly Macro original;
        private readonly ComboBox mode = new FluentComboBox();
        private readonly FluentTabs tabs = new FluentTabs();
        private readonly Label durationCaption = Caption("");
        private readonly NumericUpDown mainDuration = Number(1), mainGap = Number(0);
        private readonly DataGridView before = NewGrid(), after = NewGrid();
        private readonly Label note = new Label();
        private readonly Label modeHelp = new Label();
        private readonly Action<Action<int, int>> preparePoint;
        private readonly Action cancelPoint;
        public Macro Result { get; private set; }

        public MacroEditor(Macro macro, Action<Action<int, int>> capture, Action cancel)
        {
            original = macro.Clone(); preparePoint = capture; cancelPoint = cancel;
            Text = L.F("KeyHold · {0} 个性化设置", macro.Key);
            ClientSize = new Size(1140, 820); MinimumSize = new Size(1000, 720);
            StartPosition = FormStartPosition.CenterParent;
            TableLayoutPanel root = new FluentTableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 20, 24, 16), ColumnCount = 1, RowCount = 6 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 162));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(root);
            TableLayoutPanel heading = new FluentTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 16) };
            heading.RowStyles.Add(new RowStyle(SizeType.AutoSize)); heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label title = FluentUi.Text(L.F("个性化 · {0}", macro.Key), 24F, false); title.Font = FluentUi.Font(24F, FontStyle.Bold);
            heading.Controls.Add(title, 0, 0); heading.Controls.Add(FluentUi.Text(L.T("为这个按键编排动作与节奏。"), 10F, true), 0, 1); root.Controls.Add(heading, 0, 0);

            FluentCard mainCard = new FluentCard { Dock = DockStyle.Fill };
            TableLayoutPanel options = new FluentTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Margin = new Padding(0) };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); options.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            options.Controls.Add(FluentUi.Text(L.F("原坐标动作  ({0}, {1})", macro.X, macro.Y), 10F, false), 0, 0);
            durationCaption.Padding = new Padding(0); durationCaption.Margin = new Padding(0); durationCaption.Tag = "muted";
            options.Controls.Add(durationCaption, 1, 0); options.Controls.Add(FluentUi.Text(L.T("到后置动作间隔 ms"), 9.5F, true), 2, 0);
            mode.DropDownStyle = ComboBoxStyle.DropDownList; mode.FlatStyle = FlatStyle.Flat; mode.Width = 260; mode.Margin = new Padding(0, 3, 18, 0);
            mode.Items.AddRange(new object[] { L.T("持续按住，随松键释放"), L.T("单次点击") }); mode.SelectedIndex = macro.MouseMode == "Click" ? 1 : 0;
            mainDuration.Value = macro.MainDurationMs; mainDuration.Width = 130; mainDuration.Margin = new Padding(0, 3, 18, 0);
            mainGap.Value = macro.AfterMainDelayMs; mainGap.Width = 130; mainGap.Margin = new Padding(0, 3, 0, 0);
            options.Controls.Add(mode, 0, 1); options.Controls.Add(mainDuration, 1, 1); options.Controls.Add(mainGap, 2, 1);
            modeHelp.Dock = DockStyle.Fill; modeHelp.Tag = "muted"; modeHelp.Font = FluentUi.Font(9F, FontStyle.Regular);
            options.Controls.Add(modeHelp, 0, 2); options.SetColumnSpan(modeHelp, 3); mainCard.Controls.Add(options); root.Controls.Add(mainCard, 0, 1);

            FluentCard sequenceInfo = new FluentCard { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0, 0, 0, 10) };
            Label explanation = FluentUi.Text(L.T("前置动作  →  原坐标动作  →  后置动作\r\n短按会完成序列；新键、暂停或 F12 可取消。默认 40ms；30 帧约 33.33ms，10ms 需实测。"), 9F, true);
            explanation.Dock = DockStyle.Fill; explanation.AutoSize = false; sequenceInfo.Controls.Add(explanation); root.Controls.Add(sequenceInfo, 0, 2);
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            tabs.AddPage(Page(L.T("前置动作（原坐标之前）"), before), L.T("前置动作（原坐标之前）")); tabs.AddPage(Page(L.T("后置动作（原坐标之后）"), after), L.T("后置动作（原坐标之后）"));
            tabs.SelectedIndexChanged += delegate { cancelPoint(); }; root.Controls.Add(tabs, 0, 3);
            note.Dock = DockStyle.Fill; note.Tag = "muted"; note.Font = FluentUi.Font(9F, FontStyle.Regular); note.Padding = new Padding(0, 10, 0, 0);
            note.Text = L.T("每组最多 10 个动作。选中坐标动作后可按 F6 在目标应用中取点，不会抢回焦点。");
            root.Controls.Add(note, 0, 4);
            FlowLayoutPanel commands = new FluentFlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 9, 0, 0), Margin = new Padding(0) };
            Button save = Button(L.T("应用到该键位"), delegate { Apply(); }, 152); ((FluentButton)save).Tone = FluentButtonTone.Primary;
            Button close = Button(L.T("取消"), delegate { DialogResult = DialogResult.Cancel; Close(); }, 100);
            commands.Controls.Add(close); commands.Controls.Add(save); root.Controls.Add(commands, 0, 5); CancelButton = close;
            FormClosed += delegate { cancelPoint(); };
            Fill(before, macro.BeforeActions); Fill(after, macro.AfterActions);
            mode.SelectedIndexChanged += delegate { RefreshTiming(); };
            before.CellValueChanged += delegate { RefreshTiming(); }; before.RowsAdded += delegate { RefreshTiming(); }; before.RowsRemoved += delegate { RefreshTiming(); };
            RefreshTiming(); RefreshAppearance();
        }
        private void RefreshTiming()
        {
            bool click = mode.SelectedIndex == 1, deferred = false;
            foreach (DataGridViewRow row in before.Rows)
            {
                string kind = Convert.ToString(row.Cells["kind"].Value); int gap;
                if (kind == L.T("按键点按") || kind == L.T("点击坐标") ||
                    (kind == L.T("移动坐标") && Int32.TryParse(Convert.ToString(row.Cells["gap"].Value), out gap) && gap > 0))
                { deferred = true; break; }
            }
            durationCaption.Text = click ? L.T("点击时长 ms") : L.T("短按点击时长 ms");
            mainDuration.AccessibleName = durationCaption.Text;
            mainDuration.Enabled = click || deferred;
            modeHelp.Text = click ? L.T("按指定时长完成一次点击，再执行后置动作。间隔从动作结束开始计算。")
                : deferred ? L.T("按住时随松键释放；若在前置延迟中已松键，按“短按点击时长”完成一次点击。后置独立点击请切换为单次点击。")
                : L.T("没有前置延迟，无需设置短按点击时长。实际按住多久由实体松键决定。");
        }
        private static NumericUpDown Number(int minimum)
        { return new NumericUpDown { Minimum = minimum, Maximum = 60000, Width = 130, BorderStyle = BorderStyle.FixedSingle }; }
        private static Label Caption(string text)
        { return FluentUi.Text(text, 9.5F, true); }
        private static Button Button(string text, EventHandler click, int width)
        {
            using (Font font = FluentUi.Font(10F, FontStyle.Regular))
                width = Math.Max(width, TextRenderer.MeasureText(text, font).Width + 16);
            Button b = new FluentButton { Text = text, Width = width, Height = 36 }; b.Click += click; return b;
        }
        private static DataGridView NewGrid()
        {
            DataGridView grid = new FluentGrid { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, MultiSelect = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
            FluentUi.ConfigureGrid(grid);
            DataGridViewComboBoxColumn kind = new DataGridViewComboBoxColumn { Name = "kind", HeaderText = L.T("动作"), FillWeight = 115, FlatStyle = FlatStyle.Flat };
            kind.Items.AddRange(L.T("按键点按"), L.T("点击坐标"), L.T("移动坐标")); grid.Columns.Add(kind);
            DataGridViewComboBoxColumn key = new DataGridViewComboBoxColumn { Name = "key", HeaderText = L.T("按键"), FillWeight = 95, FlatStyle = FlatStyle.Flat };
            key.Items.Add("");
            foreach (string k in KeyCatalog.Keys.Keys) key.Items.Add(k);
            grid.Columns.Add(key);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "x", HeaderText = L.T("屏幕 X"), FillWeight = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "y", HeaderText = L.T("屏幕 Y"), FillWeight = 85 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "duration", HeaderText = L.T("按住时长 ms"), FillWeight = 105 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "gap", HeaderText = L.T("结束后间隔 ms"), FillWeight = 120 });
            grid.CurrentCellDirtyStateChanged += delegate { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += delegate(object sender, DataGridViewCellEventArgs e) { if (e.RowIndex >= 0) SetEditable(grid.Rows[e.RowIndex]); };
            grid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; };
            return grid;
        }
        private static void SetEditable(DataGridViewRow row)
        {
            string kind = Convert.ToString(row.Cells["kind"].Value);
            bool keyboard = kind == L.T("按键点按");
            DataGridViewComboBoxCell key = (DataGridViewComboBoxCell)row.Cells["key"];
            key.ReadOnly = !keyboard;
            key.DisplayStyle = keyboard ? DataGridViewComboBoxDisplayStyle.DropDownButton : DataGridViewComboBoxDisplayStyle.Nothing;
            string value = Convert.ToString(key.Value);
            if (!keyboard && value.Length > 0) { row.Tag = value; key.Value = ""; }
            else if (keyboard)
            {
                if (value.Length == 0) key.Value = row.Tag as string ?? "1";
                else row.Tag = value;
            }
            row.Cells["x"].ReadOnly = row.Cells["y"].ReadOnly = kind == L.T("按键点按");
            row.Cells["duration"].ReadOnly = kind == L.T("移动坐标");
            FluentPalette palette = FluentUi.PaletteFor(row.DataGridView);
            foreach (DataGridViewCell cell in row.Cells) cell.Style.BackColor = cell.ReadOnly ? palette.Subtle : palette.Surface;
        }
        private Panel Page(string text, DataGridView grid)
        {
            Panel page = new Panel { Text = text, Padding = new Padding(12, 10, 12, 8) };
            TableLayoutPanel body = new FluentTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            page.Controls.Add(body); body.Controls.Add(grid, 0, 0);
            FlowLayoutPanel bar = new FluentFlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0) };
            bar.Controls.Add(Button(L.T("＋按键点按"), delegate { Add(grid, L.T("按键点按")); }, 106));
            bar.Controls.Add(Button(L.T("＋坐标点击"), delegate { Add(grid, L.T("点击坐标")); }, 106));
            bar.Controls.Add(Button(L.T("＋移动坐标"), delegate { Add(grid, L.T("移动坐标")); }, 106));
            bar.Controls.Add(Button(L.T("删除"), delegate { cancelPoint(); if (grid.CurrentCell != null) grid.Rows.RemoveAt(grid.CurrentCell.RowIndex); }, 62));
            bar.Controls.Add(Button(L.T("上移"), delegate { Swap(grid, -1); }, 62));
            bar.Controls.Add(Button(L.T("下移"), delegate { Swap(grid, 1); }, 62));
            bar.Controls.Add(Button(L.T("为选中动作取点 F6"), delegate { PrepareCapture(grid); }, 174));
            body.Controls.Add(bar, 0, 1); return page;
        }
        private void Add(DataGridView grid, string kind)
        {
            if (grid.Rows.Count >= 10) { note.Text = L.T("前置和后置分别最多 10 个动作。"); return; }
            int i = grid.Rows.Add(kind, kind == L.T("按键点按") ? "1" : "", original.X, original.Y, MacroAction.DefaultTimingMs, MacroAction.DefaultTimingMs);
            grid.CurrentCell = grid.Rows[i].Cells["kind"]; SetEditable(grid.Rows[i]);
        }
        private void Swap(DataGridView grid, int delta)
        {
            cancelPoint(); grid.EndEdit();
            if (grid.CurrentCell == null) return;
            int a = grid.CurrentCell.RowIndex, b = a + delta;
            if (b < 0 || b >= grid.Rows.Count) return;
            object[] values = new object[grid.Columns.Count];
            for (int j = 0; j < values.Length; j++) values[j] = grid.Rows[a].Cells[j].Value;
            for (int j = 0; j < values.Length; j++) grid.Rows[a].Cells[j].Value = grid.Rows[b].Cells[j].Value;
            grid.Rows[b].SetValues(values); grid.CurrentCell = grid.Rows[b].Cells["kind"];
        }
        private void PrepareCapture(DataGridView grid)
        {
            if (grid.CurrentCell == null) { note.Text = L.T("请先添加并选中一个坐标动作。"); return; }
            DataGridViewRow row = grid.Rows[grid.CurrentCell.RowIndex];
            if (Convert.ToString(row.Cells["kind"].Value) == L.T("按键点按")) { note.Text = L.T("取点适用于坐标点击或移动动作。"); return; }
            preparePoint(delegate(int x, int y)
            {
                if (row.DataGridView == grid)
                { row.Cells["x"].Value = x; row.Cells["y"].Value = y; note.Text = L.F("已取点：({0}, {1})；宏仍暂停。", x, y); }
            });
            note.Text = L.T("待取点：切换到目标应用，把鼠标放到目标位置，按 F6。");
        }
        private static void Fill(DataGridView grid, List<MacroAction> actions)
        {
            foreach (MacroAction a in actions)
            {
                string kind = a.Kind == "KeyTap" ? L.T("按键点按") : a.Kind == "Click" ? L.T("点击坐标") : L.T("移动坐标");
                int i = grid.Rows.Add(kind, a.Kind == "KeyTap" ? a.Key ?? "1" : "", a.X, a.Y, a.DurationMs, a.DelayAfterMs); SetEditable(grid.Rows[i]);
            }
        }
        private static int Integer(DataGridViewRow row, string cell)
        {
            int value;
            if (!Int32.TryParse(Convert.ToString(row.Cells[cell].Value), out value)) throw new ArgumentException(L.F("第 {0} 个动作的坐标、时长和间隔必须是整数。", row.Index + 1));
            return value;
        }
        private static List<MacroAction> Read(DataGridView grid)
        {
            grid.EndEdit(); List<MacroAction> actions = new List<MacroAction>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                string kind = Convert.ToString(row.Cells["kind"].Value);
                if (kind != L.T("按键点按") && kind != L.T("点击坐标") && kind != L.T("移动坐标")) throw new ArgumentException(L.T("请选择有效的动作类型。"));
                MacroAction a = new MacroAction { Kind = kind == L.T("按键点按") ? "KeyTap" : kind == L.T("点击坐标") ? "Click" : "Move",
                    Key = kind == L.T("按键点按") ? Convert.ToString(row.Cells["key"].Value) : "", DelayAfterMs = Integer(row, "gap") };
                if (a.Kind != "KeyTap") { a.X = Integer(row, "x"); a.Y = Integer(row, "y"); }
                if (a.Kind != "Move") a.DurationMs = Integer(row, "duration");
                a.Validate(); actions.Add(a);
            }
            return actions;
        }
        private void Apply()
        {
            try
            {
                Macro result = original.Clone(); result.MouseMode = mode.SelectedIndex == 1 ? "Click" : "Hold";
                result.MainDurationMs = (int)mainDuration.Value; result.AfterMainDelayMs = (int)mainGap.Value;
                result.BeforeActions = Read(before); result.AfterActions = Read(after); result.ValidateActions();
                Result = result; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, L.T("KeyHold 个性化设置"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
