namespace HotSockets
{
    static class HotHelpers
    {
        public static void MustSucceed(bool result, string operationName)
        {
            if (result)
                return;

            throw new HotSocketException($"Operation '{operationName}' should have succeeded but did not.");
        }
    }
}
