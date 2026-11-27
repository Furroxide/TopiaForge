using System;
using System.Runtime.Serialization;

namespace TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Crypto
{
    /// <summary>This exception is thrown whenever we find something we don't expect in a message.</summary>
    [Serializable]
    internal class InvalidCipherTextException
        : CryptoException
    {
        public InvalidCipherTextException()
            : base()
        {
        }

        public InvalidCipherTextException(string message)
            : base(message)
        {
        }

        public InvalidCipherTextException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        protected InvalidCipherTextException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
