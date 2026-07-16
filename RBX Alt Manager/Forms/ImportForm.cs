using RBX_Alt_Manager.Forms;
using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    public partial class ImportForm : Form
    {
        public ImportForm()
        {
            AccountManager.SetDarkBar(Handle);

            InitializeComponent();
            this.Rescale();
        }

        public void ApplyTheme()
        {
            BackColor = ThemeEditor.FormsBackground;
            ForeColor = ThemeEditor.FormsForeground;

            foreach (Control control in this.Controls)
            {
                if (control is Button || control is CheckBox)
                {
                    if (control is Button)
                    {
                        Button b = control as Button;
                        b.FlatStyle = ThemeEditor.ButtonStyle;
                        b.FlatAppearance.BorderColor = ThemeEditor.ButtonsBorder;
                    }

                    if (!(control is CheckBox)) control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
                else if (control is TextBox || control is RichTextBox)
                {
                    if (control is Classes.BorderedTextBox)
                    {
                        Classes.BorderedTextBox b = control as Classes.BorderedTextBox;
                        b.BorderColor = ThemeEditor.TextBoxesBorder;
                    }

                    if (control is Classes.BorderedRichTextBox)
                    {
                        Classes.BorderedRichTextBox b = control as Classes.BorderedRichTextBox;
                        b.BorderColor = ThemeEditor.TextBoxesBorder;
                    }

                    control.BackColor = ThemeEditor.TextBoxesBackground;
                    control.ForeColor = ThemeEditor.TextBoxesForeground;
                }
                else if (control is Label)
                {
                    control.BackColor = ThemeEditor.LabelTransparent ? Color.Transparent : ThemeEditor.LabelBackground;
                    control.ForeColor = ThemeEditor.LabelForeground;
                }
                else if (control is ListBox)
                {
                    control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
            }
        }

        private void ImportForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private async void ImportButton_Click(object sender, EventArgs e)
        {
            string[] List = Accounts.Text.Split('\n');

            Accounts.Text = string.Empty;
            ImportButton.Enabled = false;

            var Output = new System.Text.StringBuilder();
            int Done = 0;

            foreach (string Token in List)
            {
                if (!string.IsNullOrWhiteSpace(Token))
                {
                    // Each AddAccount does a synchronous Roblox HTTP call; run it off the UI thread so the form
                    // doesn't freeze while importing a long list. Results stream in as each one completes.
                    Account NewAccount = await Task.Run(() => AccountManager.AddAccount(Token.Trim()));

                    Output.AppendLine(NewAccount != null ? $"Added {NewAccount.Username}" : $"Failed to add {Token.Trim()}");
                    Accounts.Text = Output.ToString();
                }

                ImportButton.Text = $"Importing {++Done}/{List.Length}";
            }

            ImportButton.Text = "Import";
            ImportButton.Enabled = true;
        }
    }
}