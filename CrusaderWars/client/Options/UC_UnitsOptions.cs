using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Runtime.Versioning;

namespace CrusaderWars.client
{
    [SupportedOSPlatform("windows")]
    public partial class UC_UnitsOptions : UserControl
    {
        private readonly CheckBox statFidelity = new CheckBox
        {
            Name = "OptionSelection_StatFidelityMode",
            Text = "Preserve CK3 combat stats (experimental)",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10F),
            Padding = new Padding(6),
            AccessibleDescription = "Use exported CK3 damage, toughness and commander bonuses when available. Missing data is listed in the battle conversion report."
        };

        public UC_UnitsOptions()
        {
            InitializeComponent();
            Controls.Add(statFidelity);
            Controls.SetChildIndex(tableLayoutPanel1, 0);
            toolTip1.SetToolTip(statFidelity, statFidelity.AccessibleDescription);
        }

        public void SetStatFidelityMode(string value) => statFidelity.Checked = value == "Enabled" || value == "Validated";
        public string GetStatFidelityMode() => statFidelity.Checked ? "Enabled" : "Disabled";
    }
}
