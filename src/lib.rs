pub mod conversion;
pub mod docx;
pub mod lists;
pub mod settings;
pub mod storage;
pub mod tags;
#[cfg(windows)]
mod word;
pub mod workspace;

pub fn tr<'a>(english: bool, french: &'a str, en: &'a str) -> &'a str {
    if english { en } else { french }
}
