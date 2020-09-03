using System;

namespace HotSockets
{

    [Serializable]
    public class HotSocketException : Exception
    {
        public HotSocketException() { }
        public HotSocketException(string message) : base(message) { }
        public HotSocketException(string message, Exception inner) : base(message, inner) { }
        protected HotSocketException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
