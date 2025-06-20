namespace Better_SignalRGB_Screen_Capture.Activation;

public interface IActivationHandler
{
    bool CanHandle(object args);

    Task HandleAsync(object args);
}
