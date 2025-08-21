using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using RSoft.MacroPad.BLL;
using RSoft.MacroPad.BLL.Infrasturture;
using RSoft.MacroPad.BLL.Infrasturture.Configuration;
using RSoft.MacroPad.BLL.Infrasturture.Model;
using RSoft.MacroPad.BLL.Infrasturture.Physical;
using RSoft.MacroPad.BLL.Infrasturture.Protocol;
using RSoft.MacroPad.BLL.Infrasturture.Protocol.Mappers;
using RSoft.MacroPad.BLL.Infrasturture.UsbDevice;
using RSoft.MacroPad.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace RSoft.MacroPad.Forms
{
    public partial class MainForm : Form
    {
        private KeyboardLayout[] _layouts;
        private LayoutParser _parser = new LayoutParser();
        private IUsb _usb = new HidLibUsb();
        private ConfigurationReader _configReader = new ConfigurationReader();
        private ComposerRepository _composerRepository = new ComposerRepository();
        private SplitContainer _editorAndListSplit;
        private GroupBox _grpAssignments;
        private ListView _lvAssignments;
        private bool _initialSplitSet;

        public MainForm()
        {
            InitializeComponent();
            BuildAssignmentListView();
           // EnsureKeySetupVisible();   // <= add this line
            keyboardVisual1.FunctionSelected += (s, action) =>
            {
                var layer = keyboardVisual1.Layer;
                foreach (ListViewItem row in _lvAssignments.Items)
                {
                    var key = ((byte Layer, InputAction Action))row.Tag;
                    row.Selected = key.Layer == layer && key.Action.Equals(action);
                    if (row.Selected) { row.EnsureVisible(); }
                }
            };
            InitializeLayouts();
            InitializeUsb();
        }

        private void PopulateAssignmentsList()
        {
            _lvAssignments.BeginUpdate();
            _lvAssignments.Items.Clear();

            var layout = keyboardVisual1.KeyboardLayout;
            if (layout == null)
            {
                _lvAssignments.EndUpdate();
                return;
            }

            const int layerCount = 3; // your board has 3 layers

            foreach (PhysicalControl control in layout.Controls)
            {
                foreach (var action in control.Actions)
                {
                    for (byte layer = 1; layer <= layerCount; layer++)
                    {
                        var lvi = new ListViewItem(new[]
                            {
                                layer.ToString(),
                                action.ToString(),
                                "—",   // Type (unassigned yet)
                                "—"    // Value (unassigned yet)
                            })
                            { Tag = (layer, action) };

                        _lvAssignments.Items.Add(lvi);
                    }
                }
            }

            _lvAssignments.EndUpdate();
        }

        #region Init
        private void InitializeUsb()
        {
            var config = _configReader.Read("config.txt");
            if (config != null)
                _usb.SupportedDevices = config.SupportedDevices;

            _usb.OnConnected += (s, e) =>
            {
                var layout = _layouts.FirstOrDefault(l => l.Products.Any(p => p.VendorId == _usb.VendorId && p.ProductId == _usb.ProductId));

                if (layout != null)
                {
                    keyboardVisual1.KeyboardLayout = layout;
                    keyboardFunction1.KeyboardLayout = layout;

                    PopulateAssignmentsList();   // <--- add this line
                }

                lblCommStatus.Text = $"Connected: ({_usb.VendorId}:{_usb.ProductId}) Protocol: {_usb.ProtocolType}.id{_usb.Version}";
                SeedRowsFromLayout();         // <— add this
                DumpAllSlotsToConsole();      // <— and this
                ShowDisclaimerIfNeeded();
            };
        }


        private void ShowDisclaimerIfNeeded()
        {
            if (!TestedProducts.IsTested(_usb.VendorId, _usb.ProductId))
            {
                new DisclaimerForm().ShowDialog();
            }
        }

        private void SetUsbStatus(bool connected)
        {
            lblStatus.Text = connected ? "Connected" : "Disconnected";
            lblStatus.BackColor = connected ? Color.FromArgb(0, 128, 0) : Color.FromArgb(128, 0, 0);
            tsSend.Enabled = connected;
        }

        //private void tsWriteAll_Click(object sender, EventArgs e)
        //{
        //    StopRecording(sender, e);

        //    if (_assignments.Count == 0)
        //    {
        //        MessageBox.Show("No assignments to send.");
        //        return;
        //    }

        //    var composer = _composerRepository.Get(_usb.ProtocolType, _usb.Version);
        //    bool success = true;

        //    foreach (var rec in _assignments.Values)
        //    {
        //        IEnumerable<Report> reports = Enumerable.Empty<Report>();

        //        switch (rec.Type)
        //        {
        //            case Model.SetFunction.KeySequence:
        //                reports = composer.Key(
        //                    rec.Action,
        //                    rec.Layer,
        //                    keyboardFunction1.Delay, // you may want to store Delay in rec as well
        //                    rec.Value.Split(' ').Select(vk => (/*MapKeyCode*/ KeyCodeMapper.Map((VirtualKey)int.Parse(vk)), ModifierMapper.None))
        //                );
        //                break;

        //            case Model.SetFunction.MediaKey:
        //                reports = composer.Media(rec.Action, rec.Layer, MediaKeyMapper.Map((VirtualKey)Enum.Parse(typeof(VirtualKey), rec.Value)));
        //                break;

        //            case Model.SetFunction.Mouse:
        //                // parse back from Value string if needed
        //                break;

        //            case Model.SetFunction.LED:
        //                // same idea – parse back from Value string
        //                break;
        //        }

        //        foreach (var report in reports)
        //        {
        //            if (!_usb.Write(report))
        //            {
        //                success = false;
        //                Console.WriteLine($"Write failed for {rec.Action} on L{rec.Layer}");
        //                break;
        //            }
        //        }

        //        if (!success) break;
        //    }

        //    lblCommStatus.Text = success ? "WriteAll successful" : "WriteAll failed";
        //    lblCommStatus.Text += $" [{DateTime.Now:T}]";
        //}

        private void SaveProfile(string path)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_assignments.Values);
            File.WriteAllText(path, json);
        }

        private void LoadProfile(string path)
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<AssignmentRecord>>(File.ReadAllText(path));
            _assignments.Clear();
            foreach (var rec in list)
                _assignments[(rec.Layer, rec.Action)] = rec;

            // refresh ListView
            foreach (ListViewItem row in _lvAssignments.Items)
            {
                var key = ((byte Layer, InputAction Action))row.Tag;
                if (_assignments.TryGetValue(key, out var rec))
                {
                    row.SubItems[2].Text = rec.Type.ToString();
                    row.SubItems[3].Text = rec.Value;
                }
            }
        }

        private void SafeSetSplitterDistance()
{
    if (_editorAndListSplit == null) return;

    int h = _editorAndListSplit.Height;
    if (h <= 0) return;

    int minTop = _editorAndListSplit.Panel1MinSize;
    int maxTop = h - _editorAndListSplit.Panel2MinSize - _editorAndListSplit.SplitterWidth;
    if (maxTop < minTop) return; // too small; let WinForms handle it

    int target = (int)(h * 0.55);               // ~55% to Panel1 (Key setup)
    if (target < minTop) target = minTop;
    if (target > maxTop) target = maxTop;

    if (_editorAndListSplit.SplitterDistance != target)
        _editorAndListSplit.SplitterDistance = target;
}

        private void InitializeLayouts()
        {
            _layouts = _parser.Parse("layouts.txt");
            ((ToolStripDropDownMenu)tsLayout.DropDown).ShowImageMargin = false;
            tsLayout.DropDownItems.Clear();
            tsLayout.DropDownItems.AddRange(_layouts.Select(l =>
            {
                var result = new ToolStripMenuItem()
                {
                    Text = l.Name,
                    AutoSize = true,
                    Tag = l
                };

                result.Click += (s, e) =>
                {
                    StopRecording(s, e);
                    keyboardVisual1.KeyboardLayout = l;
                    keyboardFunction1.KeyboardLayout = l;
                    PopulateAssignmentsList();   // ensure the list refreshes when switching layouts
                    SeedRowsFromLayout();      // ← add this
                    DumpAllSlotsToConsole();   // ← and this
                };

                return result;

            }).ToArray());
        }

        private void Tick(object sender, EventArgs e)
        {
            SetUsbStatus(_usb.Connect());
        }

        private void StopRecording(object sender, EventArgs e)
        {
            keyboardFunction1.StopRecording();
        }

        #endregion;  

        private void tsSend_Click(object sender, EventArgs e)
        {
            // *****************************************************************
            // sanity: are we starting with a selected action?
            if (keyboardVisual1.SelectedAction == InputAction.None)
            {
                MessageBox.Show("Please select a key or knob action to map!");
                return;
            }

            // what does the editor have?
            var seq = keyboardFunction1.KeySequence.ToList();
            System.Diagnostics.Debug.WriteLine($"[DEBUG] UI sequence count = {seq.Count}");

            foreach (var (s, i) in seq.Select((k, i) => (k, i)))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[DEBUG] [{i}] scan={s.ScanCode} mods=Ctrl({s.CtrlL || s.CtrlR}) Shift({s.ShiftL || s.ShiftR}) Alt({s.AltL || s.AltR}) Win({s.WinL || s.WinR})");
            }

            // ****************************************************************
            StopRecording(sender, e);
            if (keyboardVisual1.SelectedAction == InputAction.None)
            {
                MessageBox.Show("Please select a key or knob action to map!");
            }
            var composer = _composerRepository.Get(_usb.ProtocolType, _usb.Version);

            IEnumerable<Report> reports = Enumerable.Empty<Report>();
            switch (keyboardFunction1.Function)
            {
                case Model.SetFunction.LED:
                    reports = composer.Led(keyboardVisual1.Layer, keyboardFunction1.LedMode, keyboardFunction1.LedColor);
                    break;
                case Model.SetFunction.KeySequence:
                    var currentLayout = PInvoke.GetKeyboardLayout(0);
                    var enUsLayout = PInvoke.LoadKeyboardLayout("00000409", ACTIVATE_KEYBOARD_LAYOUT_FLAGS.KLF_ACTIVATE);

                    reports = composer.Key(keyboardVisual1.SelectedAction, keyboardVisual1.Layer, keyboardFunction1.Delay,
                        keyboardFunction1.KeySequence.Select(s => (
                        KeyCodeMapper.Map((VirtualKey)PInvoke.MapVirtualKeyEx((uint)s.ScanCode, MAP_VIRTUAL_KEY_TYPE.MAPVK_VSC_TO_VK, enUsLayout)),
                        ModifierMapper.Map(s.ShiftL, s.ShiftR, s.AltL, s.AltR, s.CtrlL, s.CtrlR, s.WinL, s.WinR))));
                    PInvoke.ActivateKeyboardLayout(currentLayout, ACTIVATE_KEYBOARD_LAYOUT_FLAGS.KLF_ACTIVATE);
                    break;
                case Model.SetFunction.MediaKey:
                    reports = composer.Media(keyboardVisual1.SelectedAction, keyboardVisual1.Layer, MediaKeyMapper.Map((VirtualKey)keyboardFunction1.MediaKey));
                    break;
                case Model.SetFunction.Mouse:
                    reports = composer.Mouse(keyboardVisual1.SelectedAction, keyboardVisual1.Layer, keyboardFunction1.MouseButton, keyboardFunction1.MouseModifier);
                    break;
            }
            bool success = true;
            HidLog.ClearLog();
            // ****************************************************************
            var list = reports.ToList();
            System.Diagnostics.Debug.WriteLine($"[LEGACY] sending {list.Count} reports");
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                var hex = BitConverter.ToString(r.Data);
                System.Diagnostics.Debug.WriteLine($"[LEGACY] #{i:D2} id=0x{r.ReportId:X2} data={hex}");
            }


            // *****************************************************************
            foreach (var report in reports)
            {
                if (!_usb.Write(report))
                {
                    success = false;
                    break;
                }
            }
            lblCommStatus.Text = success
                ? "Writing successful"
                : "Write failed";
            lblCommStatus.Text += $" [{DateTime.Now.ToString("T")}]";
            if (success)
            {
                UpsertAssignmentFromUI();
            }
            

        }

        private void tsAbout_Click(object sender, EventArgs e)
        {
            StopRecording(sender, e);
            var aboutBox = new AboutBox();
            aboutBox.ShowDialog();
        }

        private void tsSetParams_Click(object sender, EventArgs e)
        {
            var f = new ConnectionForm(_usb);
            f.ShowDialog();
        }

        // --- Assignment model kept only in UI memory ---
        private sealed class AssignmentRecord
        {
            public byte Layer { get; init; }
            public InputAction Action { get; init; }
            public Model.SetFunction Type { get; init; }
            public string Value { get; init; } = "—";
        }

        // Key for dictionary lookups
        private readonly Dictionary<(byte Layer, InputAction Action), AssignmentRecord> _assignments
            = new Dictionary<(byte, InputAction), AssignmentRecord>();



        // Build the ListView UI at runtime (no Designer changes required)
        private void BuildAssignmentListView()
        {
            // Build the ListView + bottom group
            _lvAssignments = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true
            };
            _lvAssignments.Columns.Add("Layer", 70);
            _lvAssignments.Columns.Add("Slot", 180);
            _lvAssignments.Columns.Add("Type", 140);
            _lvAssignments.Columns.Add("Value", 800);

            _grpAssignments = new GroupBox
            {
                Text = "Assignments",
                Dock = DockStyle.Fill
            };
            _grpAssignments.Controls.Add(_lvAssignments);

            // Parent is the existing Panel2 from your main split (created by the Designer)
            var panel2 = splitContainer1.Panel2;

            panel2.SuspendLayout();

            // Create nested split — DO NOT touch Panel2MinSize / SplitterDistance yet
            _editorAndListSplit = new SplitContainer();
            ((System.ComponentModel.ISupportInitialize)_editorAndListSplit).BeginInit();
            _editorAndListSplit.Dock = DockStyle.Fill;
            _editorAndListSplit.Orientation = Orientation.Horizontal;
            _editorAndListSplit.SplitterWidth = 6;

            // Move groupBox3 (Key setup) into Panel1 of the nested split
            panel2.Controls.Remove(groupBox3);
            panel2.Controls.Add(_editorAndListSplit);
            ((System.ComponentModel.ISupportInitialize)_editorAndListSplit).EndInit();

            groupBox3.Dock = DockStyle.Fill;
            _editorAndListSplit.Panel1.Controls.Add(groupBox3);

            // Bottom: assignments list
            _editorAndListSplit.Panel2.Controls.Add(_grpAssignments);

            panel2.ResumeLayout();

            // Defer size-sensitive settings until after layout
            this.Shown += (_, __) =>
            {
                _editorAndListSplit.Panel2MinSize = 160;          // set now that we have size
                _editorAndListSplit.FixedPanel = FixedPanel.Panel2;
                SafeSetSplitterDistance();                        // initial distance (~55% top)
            };

            _editorAndListSplit.SizeChanged += (_, __) =>
            {
                // Keep it sensible on first layout / resizes
                SafeSetSplitterDistance();
            };
        }


        //private void EnsureKeySetupVisible()
        //{
        //    // Bottom-most: assignments list
        //    _grpAssignments.Dock = DockStyle.Bottom;
        //    _grpAssignments.Height = 220;            // adjust to taste
        //    _grpAssignments.MinimumSize = new Size(0, 160);

        //    // Just above it: the existing Key setup area (keyboardFunction1 lives here)
        //    groupBox3.Dock = DockStyle.Bottom;
        //    groupBox3.Height = 260;                  // adjust to taste
        //    groupBox3.MinimumSize = new Size(0, 180);

        //    // Make sure ordering is: [top content] ... groupBox3 ... _grpAssignments ... status bar
        //    this.Controls.SetChildIndex(_grpAssignments, 0); // bottom
        //    this.Controls.SetChildIndex(groupBox3, 0);       // just above assignments
        //}


        // Fill the table with one row for EVERY action in the current layout (all layers)
        private void SeedRowsFromLayout()
        {
            _lvAssignments.BeginUpdate();
            _lvAssignments.Items.Clear();
            _assignments.Clear();

            var layout = keyboardVisual1.KeyboardLayout;
            if (layout == null)
            {
                _lvAssignments.EndUpdate();
                return;
            }

            // KeyboardLayout has LayerCount in your codebase
            var layerCount =3;

            // layout.Controls are PhysicalControl-derived and expose .Actions
            foreach (PhysicalControl control in layout.Controls)
            {
                foreach (var action in control.Actions)
                {
                    for (byte layer = 1; layer <= layerCount; layer++)
                    {
                        var rec = new AssignmentRecord
                        {
                            Layer = layer,
                            Action = action,
                            Type = Model.SetFunction.KeySequence,
                            Value = "—"
                        };
                        _assignments[(layer, action)] = rec;

                        var lvi = new ListViewItem(new[]
                            {
                                layer.ToString(),
                                action.ToString(), // e.g., "Key1", "Knob1_CW"
                                "—",
                                "—"
                            })
                            { Tag = (layer, action) };

                        _lvAssignments.Items.Add(lvi);
                    }
                }
            }

            _lvAssignments.EndUpdate();
        }

        private void DumpAllSlotsToConsole()
        {
            var layout = keyboardVisual1.KeyboardLayout;
            if (layout == null)
            {
                Debug.WriteLine("No layout loaded.");
                return;
            }

            const int layerCount = 3; // your hardware has 3 layers

            Debug.WriteLine($"--- Dump for layout {layout.Name} ---");

            foreach (PhysicalControl control in layout.Controls)
            {
                foreach (var action in control.Actions)
                {
                    for (byte layer = 1; layer <= layerCount; layer++)
                    {
                        if (_assignments.TryGetValue((layer, action), out var rec))
                        {
                            Debug.WriteLine($"L{layer} | {action} | {rec.Type} | {rec.Value}");
                        }
                        else
                        {
                            Debug.WriteLine($"L{layer} | {action} | — | —");
                        }
                    }
                }
            }
        }


        // Called after a successful Send to store/update the assignment and refresh that row
        private void UpsertAssignmentFromUI()
        {
            var layer = keyboardVisual1.Layer;                   // current layer
            var action = keyboardVisual1.SelectedAction;         // selected key/knob action
            var type = keyboardFunction1.Function;               // KeySequence / MediaKey / Mouse / LED

            string valueText = type switch
            {
                Model.SetFunction.KeySequence => string.Join(" ", keyboardFunction1.KeySequence.Select(k =>
                    $"{(k.CtrlL || k.CtrlR ? "Ctrl+" : "")}{(k.ShiftL || k.ShiftR ? "Shift+" : "")}{(k.AltL || k.AltR ? "Alt+" : "")}{(k.WinL || k.WinR ? "Win+" : "")}{k.ScanCode}")),
                Model.SetFunction.MediaKey => keyboardFunction1.MediaKey.ToString(),
                Model.SetFunction.Mouse => $"{keyboardFunction1.MouseButton} {keyboardFunction1.MouseModifier}",
                Model.SetFunction.LED => $"{keyboardFunction1.LedMode} {(keyboardFunction1.LedColor)}",
                _ => "—"
            };

            var rec = new AssignmentRecord
            {
                Layer = layer,
                Action = action,
                Type = type,
                Value = valueText
            };
            _assignments[(layer, action)] = rec;

            // update the row in-place
            foreach (ListViewItem row in _lvAssignments.Items)
            {
                var key = ((byte Layer, InputAction Action))row.Tag;
                if (key.Layer == layer && key.Action.Equals(action))
                {
                    row.SubItems[2].Text = type.ToString();
                    row.SubItems[3].Text = valueText;
                    break;
                }
            }
        }


    }
}
