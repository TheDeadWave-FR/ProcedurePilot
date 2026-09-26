//! Word automation via IDispatch, implemented entirely in Rust.
use anyhow::{Context, Result};
use std::path::Path;
use windows::{
    Win32::System::{Com::*, Ole::DISPID_PROPERTYPUT},
    core::{GUID, HSTRING, PCWSTR, VARIANT},
};

struct Apartment;
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe {
            CoUninitialize();
        }
    }
}
struct Word(IDispatch);
impl Drop for Word {
    fn drop(&mut self) {
        let _ = invoke(&self.0, "Quit", DISPATCH_METHOD, vec![VARIANT::from(0i32)]);
    }
}
fn invoke(
    object: &IDispatch,
    name: &str,
    flags: DISPATCH_FLAGS,
    mut args: Vec<VARIANT>,
) -> Result<VARIANT> {
    let name = HSTRING::from(name);
    let ptr = PCWSTR(name.as_ptr());
    let mut id = 0;
    unsafe {
        object.GetIDsOfNames(&GUID::zeroed(), &ptr, 1, 0x0409, &mut id)?;
    }
    args.reverse();
    let mut property = DISPID_PROPERTYPUT;
    let params = DISPPARAMS {
        rgvarg: args.as_mut_ptr(),
        cArgs: args.len() as u32,
        rgdispidNamedArgs: if flags == DISPATCH_PROPERTYPUT {
            &mut property
        } else {
            std::ptr::null_mut()
        },
        cNamedArgs: u32::from(flags == DISPATCH_PROPERTYPUT),
    };
    let mut result = VARIANT::default();
    unsafe {
        object.Invoke(
            id,
            &GUID::zeroed(),
            0x0409,
            flags,
            &params,
            Some(&mut result),
            None,
            None,
        )?;
    }
    Ok(result)
}
pub fn convert(input: &Path, output: &Path) -> Result<()> {
    unsafe {
        CoInitializeEx(None, COINIT_APARTMENTTHREADED).ok()?;
    }
    let _apartment = Apartment;
    let id = unsafe { CLSIDFromProgID(&HSTRING::from("Word.Application")) }
        .context("Microsoft Word introuvable / Word not installed")?;
    let word = Word(unsafe { CoCreateInstance(&id, None, CLSCTX_LOCAL_SERVER) }?);
    invoke(
        &word.0,
        "Visible",
        DISPATCH_PROPERTYPUT,
        vec![VARIANT::from(false)],
    )?;
    invoke(
        &word.0,
        "DisplayAlerts",
        DISPATCH_PROPERTYPUT,
        vec![VARIANT::from(0i32)],
    )?;
    invoke(
        &word.0,
        "AutomationSecurity",
        DISPATCH_PROPERTYPUT,
        vec![VARIANT::from(3i32)],
    )?;
    let documents =
        IDispatch::try_from(&invoke(&word.0, "Documents", DISPATCH_PROPERTYGET, vec![])?)?;
    let document = IDispatch::try_from(&invoke(
        &documents,
        "Open",
        DISPATCH_METHOD,
        vec![
            VARIANT::from(input.to_string_lossy().as_ref()),
            VARIANT::from(false),
            VARIANT::from(true),
            VARIANT::from(false),
        ],
    )?)?;
    let result = invoke(
        &document,
        "ExportAsFixedFormat",
        DISPATCH_METHOD,
        vec![
            VARIANT::from(output.to_string_lossy().as_ref()),
            VARIANT::from(17i32),
        ],
    );
    let _ = invoke(
        &document,
        "Close",
        DISPATCH_METHOD,
        vec![VARIANT::from(0i32)],
    );
    result?;
    crate::conversion::validate_pdf(output)
}
