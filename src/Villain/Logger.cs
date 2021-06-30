using System;

namespace Villain
{
    public interface ILogger
    {
        void Error(string msg, Exception e = null)
        {
        }

        void Warn(string msg, Exception e = null)
        {
        }

        void Info(string msg)
        {
        }

        void Debug(string msg)
        {
        }
    }
}
