namespace PhoneCam;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string language = Settings.Load().Language;
        try
        {
            AppFiles.Prepare();
        }
        catch (Exception ex)
        {
            string error = ex.Message == "errors.missingPayload"
                ? UiLocale.Get(language, "native.missingPayload")
                : ex.Message;
            MessageBox.Show(UiLocale.Get(language, "native.prepareFailed", ("folder", Settings.Folder), ("error", error)),
                "PhoneCam", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm(args));
    }
}
