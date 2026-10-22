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
            Text = "CK3 combat stats (experimental)",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10F),
            Padding = new Padding(6),
            AccessibleDescription = "Uses native MAA and owner-levy bonuses, commander advantage, and knight prowess/efficiency to generate unit stats. Knight damage and health budgets use a same-army reference and may change represented body counts. Requires a complete current save/export and supported mapping. Absolute balance remains experimental; counters, terrain combat formulas, rolls and pursuit/screen are not reproduced."
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
