use adblock::{lists::{FilterSet, ParseOptions, RuleTypes}, request::Request, Engine};
use std::{
    ffi::{c_char, c_int, c_void, CStr},
    panic::{catch_unwind, AssertUnwindSafe},
};

struct AdblockHandle(Engine);

#[no_mangle]
pub extern "C" fn xtreme_adblock_engine_create(filter_text: *const c_char) -> *mut c_void {
    if filter_text.is_null() {
        return std::ptr::null_mut();
    }

    catch_unwind(AssertUnwindSafe(|| unsafe {
        // SAFETY: callers pass a valid, NUL-terminated UTF-8 string for this call.
        let Ok(filter_text) = CStr::from_ptr(filter_text).to_str() else {
            return std::ptr::null_mut();
        };
        let mut filter_set = FilterSet::new(false);
        filter_set.add_filter_list(
            filter_text.to_owned(),
            ParseOptions { rule_types: RuleTypes::NetworkOnly, ..ParseOptions::default() },
        );
        Box::into_raw(Box::new(AdblockHandle(Engine::new_with_filter_set(filter_set)))).cast()
    }))
    .unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub extern "C" fn xtreme_adblock_engine_should_block(
    handle: *const c_void,
    url: *const c_char,
    source_url: *const c_char,
    request_type: *const c_char,
    method: *const c_char,
) -> c_int {
    if handle.is_null() || url.is_null() || source_url.is_null() || request_type.is_null() || method.is_null() {
        return 0;
    }

    catch_unwind(AssertUnwindSafe(|| unsafe {
        // SAFETY: callers pass valid, NUL-terminated UTF-8 strings and a live engine handle.
        let Ok(url) = CStr::from_ptr(url).to_str() else { return 0; };
        let Ok(source_url) = CStr::from_ptr(source_url).to_str() else { return 0; };
        let Ok(request_type) = CStr::from_ptr(request_type).to_str() else { return 0; };
        let Ok(method) = CStr::from_ptr(method).to_str() else { return 0; };
        let engine = &(*(handle.cast::<AdblockHandle>())).0;
        let Ok(request) = Request::new(url, source_url, request_type, method) else { return 0; };
        if engine.check_network_request(&request).should_block() { 1 } else { 0 }
    }))
    .unwrap_or(0)
}

#[no_mangle]
pub extern "C" fn xtreme_adblock_engine_destroy(handle: *mut c_void) {
    if !handle.is_null() {
        // SAFETY: this pointer was returned by xtreme_adblock_engine_create and is destroyed once.
        let _ = catch_unwind(AssertUnwindSafe(|| unsafe { drop(Box::from_raw(handle.cast::<AdblockHandle>())) }));
    }
}
