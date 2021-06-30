using System;

namespace Villain
{
    public interface IHealthEventsRegistrar
    {
        event EventHandler OnSuccess;
        event EventHandler OnFailure;
    }

    public interface IHealthEventsEmitter
    {
        void EmitOnSuccess(object sender, EventArgs args);
        void EmitOnFailure(object sender, EventArgs args);
    }

    public class HealthEvents : IHealthEventsRegistrar, IHealthEventsEmitter
    {
        public event EventHandler OnSuccess;
        public event EventHandler OnFailure;
        public void EmitOnSuccess(object sender, EventArgs e) => OnSuccess?.Invoke(sender, e);
        public void EmitOnFailure(object sender, EventArgs e) => OnFailure?.Invoke(sender, e);
    }
}
