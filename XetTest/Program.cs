// See https://aka.ms/new-console-template for more information
using  XetSharp ;


        var c = new Client();
        Console.WriteLine(c.NativeAdd(1, 2));
        Console.WriteLine(c.NativeSub(5, 3));
        Console.WriteLine(c.NativeMulti(4, 2));
        Console.WriteLine(c.NativeDiv(8, 2));
        Console.WriteLine(c.NativeDiv(8, 0));
