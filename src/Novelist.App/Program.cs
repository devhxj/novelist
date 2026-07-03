using Novelist.App.Hosting;
using Novelist.App.Desktop;

if (PhotinoLaunchMode.ShouldLaunchDesktop(args))
{
    var desktopApplication = new PhotinoDesktopApplication(new PhotinoWindowFactory());
    await desktopApplication.RunAsync(args);
    return;
}

var app = NovelistAppBuilder.Build(args);

app.Run();

public partial class Program;
