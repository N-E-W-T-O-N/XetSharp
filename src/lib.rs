//pub mod main;

#[no_mangle]
pub extern "C" fn xet_add(a: i32, b: i32) -> i32 {
    a + b
}


#[no_mangle]
pub extern "C" fn xet_sub(a: i32, b: i32) -> i32 {
    a - b
}

#[no_mangle]
pub extern "C" fn xet_multi(a: i32, b: i32) -> i32 {
    a * b
}

#[no_mangle]
pub extern "C" fn xet_div(a: i32, b: i32) -> i32 {
    if b == 0 {
        panic!("Division by zero error");
    }
    a / b
}

