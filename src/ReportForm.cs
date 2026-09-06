using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgentReaper
{
    /// <summary>結果・ドライラン内容を表示する読み取り専用ウィンドウ。</summary>
    internal sealed class ReportForm : Form
    {
        private ReportForm(string title, string body)
        {
            Text = "AgentReaper — " + title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(520, 300);
            ShowInTaskbar = true;

            var box = new TextBox();
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Dock = DockStyle.Fill;
            box.Font = new Font("Consolas", 9.75f);
            box.BackColor = Color.White;
            box.Text = body.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            box.Select(0, 0);

            var panel = new Panel();
            panel.Dock = DockStyle.Bottom;
            panel.Height = 44;

            var copy = new Button();
            copy.Text = "クリップボードにコピー";
            copy.Width = 180;
            copy.Height = 28;
            copy.Location = new Point(8, 8);
            copy.Click += delegate
            {
                try { Clipboard.SetText(box.Text); } catch { }
            };

            var close = new Button();
            close.Text = "閉じる";
            close.Width = 100;
            close.Height = 28;
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Location = new Point(panel.Width - 108, 8);
            close.Click += delegate { Close(); };

            panel.Controls.Add(copy);
            panel.Controls.Add(close);

            Controls.Add(box);
            Controls.Add(panel);

            AcceptButton = close;
        }

        public static void Show(string title, string body)
        {
            try
            {
                using (var f = new ReportForm(title, body))
                    f.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(body + "\n\n(" + ex.Message + ")", title);
            }
        }
    }
}
