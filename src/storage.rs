use anyhow::{Context, Result};
use std::{
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::Path,
    thread,
    time::{Duration, Instant},
};

pub fn xml(s: impl AsRef<str>) -> String {
    s.as_ref()
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}
pub fn now() -> String {
    chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Nanos, true)
}
pub fn read(path: &Path) -> Result<String> {
    let text =
        fs::read_to_string(path).with_context(|| format!("Lecture / Read: {}", path.display()))?;
    Ok(text.trim_start_matches('\u{feff}').to_owned())
}
pub fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    let parent = path.parent().context("Missing parent directory")?;
    let mut file = tempfile::NamedTempFile::new_in(parent)?;
    file.write_all(bytes)?;
    file.as_file().sync_all()?;
    file.persist(path)
        .map_err(|e| e.error)
        .with_context(|| format!("Écriture / Write: {}", path.display()))?;
    Ok(())
}
pub fn fingerprint(path: &Path) -> Result<Vec<u8>> {
    if !path.try_exists()? {
        return Ok(Vec::new());
    }
    let mut bytes = Vec::new();
    File::open(path)?
        .take(16 * 1024 * 1024)
        .read_to_end(&mut bytes)?;
    Ok(bytes)
}

/// Uses Windows share mode zero, matching the original C# FileShare.None lease.
/// Closing the handle releases the SMB lock, including after a process crash.
pub struct Lease {
    _file: File,
}
#[derive(Debug)]
pub struct WorkspaceBusy;
impl std::fmt::Display for WorkspaceBusy {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(
            "Un autre utilisateur modifie cet espace / Another user is modifying this workspace",
        )
    }
}
impl std::error::Error for WorkspaceBusy {}

pub fn is_busy(error: &anyhow::Error) -> bool {
    error.downcast_ref::<WorkspaceBusy>().is_some()
}
impl Lease {
    pub fn acquire(data: &Path, timeout: Duration) -> Result<Self> {
        fs::create_dir_all(data)?;
        Self::existing(data, timeout)
    }
    pub fn existing(data: &Path, timeout: Duration) -> Result<Self> {
        Self::named(data, ".procedurepilot-write.lock", timeout)
    }
    pub fn named(data: &Path, name: &str, timeout: Duration) -> Result<Self> {
        let start = Instant::now();
        loop {
            let mut opts = OpenOptions::new();
            opts.read(true).write(true).create(true).truncate(false);
            #[cfg(windows)]
            {
                use std::os::windows::fs::OpenOptionsExt;
                opts.share_mode(0);
            }
            match opts.open(data.join(name)) {
                Ok(file) => {
                    #[cfg(not(windows))]
                    if file.try_lock().is_err() {
                        if start.elapsed() >= timeout {
                            return Err(WorkspaceBusy.into());
                        }
                        thread::sleep(Duration::from_millis(75));
                        continue;
                    }
                    return Ok(Self { _file: file });
                }
                Err(e) if matches!(e.raw_os_error(), Some(32 | 33)) => {
                    if start.elapsed() >= timeout {
                        return Err(WorkspaceBusy.into());
                    }
                    thread::sleep(Duration::from_millis(75));
                }
                Err(e) => return Err(e.into()),
            }
        }
    }
}
pub fn publish(data: &Path, kind: &str) -> Result<()> {
    atomic_write(
        &data.join(".procedurepilot-change"),
        format!("{}\n{}\n{}\n", uuid::Uuid::new_v4(), now(), kind).as_bytes(),
    )
}
pub fn log(data: &Path, user: bool, message: &str) -> Result<String> {
    let _lease = Lease::named(data, ".procedurepilot-log.lock", Duration::from_secs(5))?;
    let who = if user {
        format!(
            "USER {}\\{}",
            std::env::var("USERDOMAIN").unwrap_or_default(),
            std::env::var("USERNAME").unwrap_or_default()
        )
    } else {
        "SYSTEM".into()
    };
    let line = format!("{} [{}] {}", now(), who, message.replace(['\r', '\n'], " "));
    let mut f = OpenOptions::new()
        .append(true)
        .create(true)
        .open(data.join("Logs.txt"))?;
    writeln!(f, "{line}")?;
    Ok(line)
}
