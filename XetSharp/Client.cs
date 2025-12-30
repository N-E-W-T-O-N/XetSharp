namespace XetSharp{

	public class Client
	{
		// Simulates a native method call
		public int NativeAdd(int a, int b)
		{
			return NativeMethods.xet_add(a, b);
		}

		public int NativeSub(int a, int b)
		{
			return NativeMethods.xet_sub(a, b);
		}

		public int NativeMulti(int a, int b)
		{
			return NativeMethods.xet_multi(a, b);
		}

		public int NativeDiv(int a, int b)
		{
			return NativeMethods.xet_div(a, b);
		}
	}
}