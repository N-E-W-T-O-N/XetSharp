fn main() {
    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .csharp_namespace("XetSharp")
        .csharp_dll_name("xetcore_native")
        .generate_csharp_file("XetSharp/Native/NativeMethods.g.cs")
        .unwrap();
}
