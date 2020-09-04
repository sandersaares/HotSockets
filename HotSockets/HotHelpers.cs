using System.Runtime.CompilerServices;

namespace HotSockets
{
    static class HotHelpers
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MustSucceed(bool result, string operationName)
        {
            if (result)
                return;

            throw new HotSocketException($"Operation '{operationName}' should have succeeded but did not.");
        }
    }
}
