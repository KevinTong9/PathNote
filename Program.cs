namespace PathNote;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "PathNote_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PathNote 已在运行中", "PathNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }
}