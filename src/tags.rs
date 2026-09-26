use anyhow::{Result, bail};
use std::collections::HashSet;

pub fn prefix(value: &str) -> Result<String> {
    let s = value.trim().to_ascii_uppercase();
    if s.is_empty()
        || s.len() > 16
        || !s.as_bytes()[0].is_ascii_uppercase()
        || s.ends_with('-')
        || s.contains("--")
        || !s
            .bytes()
            .all(|b| b.is_ascii_uppercase() || b.is_ascii_digit() || b == b'-')
    {
        bail!(
            "Préfixe invalide / Invalid prefix: 1–16 lettres, chiffres, tirets internes ; commencer par une lettre."
        );
    }
    Ok(s)
}

pub fn code(name: &str) -> Option<String> {
    if name.starts_with(char::is_whitespace) {
        return None;
    }
    let token = name.split_whitespace().next()?;
    let (p, n) = token.rsplit_once('-')?;
    let p = prefix(p).ok()?;
    if n.len() != 5 || !n.bytes().all(|c| c.is_ascii_digit()) || n == "00000" {
        return None;
    }
    Some(format!("{p}-{n}"))
}

pub fn number(name: &str, active: &str) -> Option<u32> {
    let c = code(name)?;
    let (p, n) = c.rsplit_once('-')?;
    if p != active {
        return None;
    }
    n.parse().ok()
}

pub fn unique<'a>(names: impl IntoIterator<Item = &'a str>) -> Result<()> {
    let mut seen = HashSet::new();
    for name in names {
        if let Some(c) = code(name)
            && !seen.insert(c.clone())
        {
            bail!("Code dupliqué / Duplicate code: {c}");
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn codes_and_prefixes() {
        assert_eq!(code("proc-it-00001 Réseau"), Some("PROC-IT-00001".into()));
        for invalid in [
            "FR-00000 x",
            "FR-00001x",
            "FR-001 x",
            "ÉT-00001 x",
            " FR-00001 x",
        ] {
            assert_eq!(code(invalid), None);
        }
        for invalid in ["", "1PROC", "P--R", "P-", "ÉT", "ABCDEFGHIJKLMNOPQ"] {
            assert!(prefix(invalid).is_err());
        }
        assert!(unique(["FR-00001 A", "fr-00001 B"]).is_err());
        assert!(unique(["FR-00001 A", "PROC-00001 B"]).is_ok());
    }
}
