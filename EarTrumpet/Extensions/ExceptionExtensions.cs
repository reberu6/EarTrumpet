using EarTrumpet.Interop;
using System;
using System.Runtime.InteropServices;

namespace EarTrumpet.Extensions
{
    public static class ExceptionExtensions
    {
        public static bool Is(this Exception ex, HRESULT type)
        {
            switch(type)
            {
                case HRESULT.AUDCLNT_E_DEVICE_INVALIDATED:
                case HRESULT.AUDCLNT_S_NO_SINGLE_PROCESS:
                case HRESULT.ERROR_NOT_FOUND:
                    return ex is COMException comException && (uint)comException.HResult == (uint)type;
                default:
                    throw new NotImplementedException();
            }
        }
    }
}
