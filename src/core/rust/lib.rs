// Placeholder proving the build/test wiring end to end; replaced by the real FFI surface in M1.
pub fn placeholder_add(a: i32, b: i32) -> i32 {
    a + b
}

#[cfg(test)]
mod tests {
    use super::placeholder_add;

    #[test]
    fn placeholder_add_adds() {
        assert_eq!(placeholder_add(2, 3), 5);
    }
}
