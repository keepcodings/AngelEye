namespace AngelEyeBmsBridge;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Releases designer-created components used by the main form.
    /// </summary>
    /// <param name="disposing">True when managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        this.components = new System.ComponentModel.Container();
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(1000, 720);
        this.Text = "Xtasys橋接應用";
        this.MinimumSize = new System.Drawing.Size(1000, 720);
        this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
    }
}
