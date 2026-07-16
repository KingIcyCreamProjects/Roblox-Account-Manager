using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Forms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RBX_Alt_Manager
{
    public partial class AccountFields : Form
    {
        Account Viewing;
        Color LastColor;

        public AccountFields()
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
                else if (control is TextBox || control is RichTextBox || control is Label)
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
                else if (control is ListBox)
                {
                    control.BackColor = ThemeEditor.ButtonsBackground;
                    control.ForeColor = ThemeEditor.ButtonsForeground;
                }
            }
        }

        private void AccountFields_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        public void FlashGreen()
        {
            LastColor = AccountName.ForeColor;
            // Plain Color.Green is near-invisible on dark themes; use a bright green there, a darker one on light.
            AccountName.ForeColor = ThemeEditor.FormsBackground.GetBrightness() < 0.5 ? Color.FromArgb(87, 245, 102) : Color.FromArgb(30, 140, 40);

            Success.Start();
        }

        private void AddField(string Field, string Value)
        {
            RemoveEmptyHint();

            Field f = new Field(Viewing, Field, Value);
            FieldsPanel.Controls.Add(f);
        }

        // Inline "no fields yet" guidance shown when an account has no custom fields (the panel was just blank before).
        private void RemoveEmptyHint()
        {
            foreach (Control hint in FieldsPanel.Controls.OfType<Label>().ToList())
                FieldsPanel.Controls.Remove(hint);
        }

        private void ShowEmptyHintIfNeeded()
        {
            if (FieldsPanel.Controls.Count == 0)
                FieldsPanel.Controls.Add(new Label { Text = "No custom fields yet — click Add to create one.", AutoSize = true, ForeColor = ThemeEditor.LabelForeground, Margin = new Padding(6) });
        }

        public void View(Account account)
        {
            if (account == null) return;

            Viewing = account;

            FieldsPanel.Controls.Clear();

            AccountName.Text = "Viewing Fields of " + account.Username;

            foreach (KeyValuePair<string, string> Key in account.Fields) AddField(Key.Key, Key.Value);

            ShowEmptyHintIfNeeded();

            Show();
            WindowState = FormWindowState.Normal;
            BringToFront();
        }

        private void AccountFields_HelpButtonClicked(object sender, CancelEventArgs e)
        {
            e.Cancel = true;

            MessageBox.Show("To edit a field, press enter on the value (right) side", "Account Fields", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Add_Click(object sender, EventArgs e)
        {
            AddField("Field", "Value");
        }

        private void Success_Tick(object sender, EventArgs e)
        {
            Success.Stop();

            AccountName.ForeColor = LastColor;
        }
    }
}