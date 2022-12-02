using System;

namespace DucoboxSilentSerial
{
    public class DucoboxCommunicationException : Exception
    {
        public DucoboxCommunicationException(string message)
            : base(message)
        {   
        }
    }
}