use crate::{docx, storage, workspace::extension};
use anyhow::{Context, Result, bail};
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::{Command, Stdio},
    thread,
    time::{Duration, Instant},
};

pub fn hidden(command: &mut Command) -> &mut Command {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(0x08000000);
    }
    command
}
pub fn wait(mut command: Command, timeout: Duration) -> Result<()> {
    let mut child = hidden(&mut command)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .context("Impossible de lancer le moteur / Unable to launch converter")?;
    let start = Instant::now();
    loop {
        if let Some(status) = child.try_wait()? {
            if !status.success() {
                bail!("Échec du moteur PDF / PDF engine failed ({status})");
            }
            return Ok(());
        }
        if start.elapsed() > timeout {
            let _ = child.kill();
            let _ = child.wait();
            bail!("Délai de conversion dépassé / Conversion timed out");
        }
        thread::sleep(Duration::from_millis(100));
    }
}
fn locate(relative: &[&str], executable: &[&str]) -> Option<PathBuf> {
    for root in ["ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA"] {
        if let Some(root) = std::env::var_os(root) {
            for p in relative {
                let p = PathBuf::from(&root).join(p);
                if p.is_file() {
                    return Some(p);
                }
            }
        }
    }
    if let Some(path) = std::env::var_os("PATH") {
        for dir in std::env::split_paths(&path) {
            for name in executable {
                let p = dir.join(name);
                if p.is_file() {
                    return Some(p);
                }
            }
        }
    }
    None
}
pub fn browser() -> Option<PathBuf> {
    locate(
        &[
            "Microsoft/Edge/Application/msedge.exe",
            "Google/Chrome/Application/chrome.exe",
            "Chromium/Application/chrome.exe",
        ],
        &["msedge.exe", "chrome.exe", "chromium", "google-chrome"],
    )
}
pub fn soffice() -> Option<PathBuf> {
    locate(
        &[
            "LibreOffice/program/soffice.exe",
            "OpenOffice 4/program/soffice.exe",
        ],
        &["soffice.exe", "soffice"],
    )
}
pub fn local_supported(input: &Path) -> bool {
    ["docx", "docm", "dotx", "dotm"].contains(&extension(input).as_str())
}
pub fn validate_pdf(path: &Path) -> Result<()> {
    let mut magic = [0u8; 5];
    fs::File::open(path)?.read_exact(&mut magic)?;
    if &magic != b"%PDF-" {
        bail!("PDF généré invalide / Invalid generated PDF");
    }
    Ok(())
}
pub fn local(input: &Path, output: &Path) -> Result<()> {
    let browser =
        browser().context("Edge/Chrome/Chromium introuvable / Browser PDF renderer not found")?;
    let temp = tempfile::tempdir()?;
    let html_path = temp.path().join("document.html");
    fs::write(&html_path, docx::render(input)?)?;
    let mut command = Command::new(browser);
    command
        .args([
            "--headless=new",
            "--disable-gpu",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-background-networking",
            "--disable-sync",
            "--no-pdf-header-footer",
            "--allow-file-access-from-files",
            "--host-resolver-rules=MAP * ~NOTFOUND",
        ])
        .arg(format!(
            "--user-data-dir={}",
            temp.path().join("profile").display()
        ))
        .arg(format!("--print-to-pdf={}", output.display()))
        .arg(
            url::Url::from_file_path(&html_path)
                .map_err(|_| anyhow::anyhow!("Invalid HTML path"))?
                .as_str(),
        );
    wait(command, Duration::from_secs(120))?;
    validate_pdf(output)
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Engine {
    Word,
    Office,
    Local,
}

// Native document layout must take precedence over the approximate HTML renderer.
fn render_with_engines(
    input: &Path,
    target: &Path,
    mut run: impl FnMut(Engine) -> Result<()>,
) -> Result<()> {
    let mut errors = vec![];
    for engine in [Engine::Word, Engine::Office, Engine::Local] {
        if (engine == Engine::Word && !cfg!(windows))
            || (engine == Engine::Local && !local_supported(input))
        {
            continue;
        }
        // An unsuccessful engine may leave a partial PDF, even with a valid header.
        if target.try_exists()? {
            fs::remove_file(target)?;
        }
        match run(engine).and_then(|()| validate_pdf(target)) {
            Ok(()) => return Ok(()),
            Err(e) => errors.push(format!("{engine:?}: {e:#}")),
        }
    }
    bail!(
        "Conversion impossible / Conversion failed. Word ou LibreOffice/OpenOffice requis ; secours DOCX : Edge/Chrome.\n{}",
        errors.join("\n")
    )
}

pub fn convert(input: &Path, output: &Path) -> Result<()> {
    let before = input.metadata()?;
    fs::read_dir(output.parent().context("Missing output parent")?)?;
    let temp = tempfile::Builder::new()
        .prefix(".procedurepilot-convert-")
        .tempdir_in(output.parent().unwrap())?;
    let target = temp.path().join("result.pdf");
    render_with_engines(input, &target, |engine| match engine {
        Engine::Word => {
            #[cfg(windows)]
            {
                // COM is isolated in this application's helper process, with a bounded lifetime.
                let mut command = Command::new(std::env::current_exe()?);
                command
                    .arg("--convert-word")
                    .arg(input)
                    .arg("--output")
                    .arg(&target)
                    .arg("--report")
                    .arg(temp.path().join("word-error.txt"));
                wait(command, Duration::from_secs(120)).with_context(|| {
                    fs::read_to_string(temp.path().join("word-error.txt"))
                        .unwrap_or_else(|_| "Export Word impossible / Word export failed".into())
                })
            }
            #[cfg(not(windows))]
            bail!("Word is Windows-only")
        }
        Engine::Office => {
            let office = soffice().context("LibreOffice/OpenOffice introuvable / Not installed")?;
            let profile = url::Url::from_directory_path(temp.path().join("office-profile"))
                .map_err(|_| anyhow::anyhow!("Invalid profile path"))?;
            let mut cmd = Command::new(office);
            cmd.arg(format!("-env:UserInstallation={profile}"))
                .args(["--headless", "--convert-to", "pdf", "--outdir"])
                .arg(temp.path())
                .arg(input);
            wait(cmd, Duration::from_secs(120))?;
            let generated = temp.path().join(format!(
                "{}.pdf",
                input.file_stem().unwrap().to_string_lossy()
            ));
            fs::copy(generated, &target)?;
            Ok(())
        }
        Engine::Local => local(input, &target),
    })?;
    let after = input.metadata()?;
    if before.len() != after.len() || before.modified()? != after.modified()? {
        bail!(
            "Document modifié pendant la conversion ; réessayez / Source changed during conversion; retry"
        );
    }
    storage::atomic_write(output, &fs::read(&target)?)
}
#[cfg(windows)]
pub fn word(input: &Path, output: &Path) -> Result<()> {
    crate::word::convert(input, output)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_export_wins_without_running_html() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("result.pdf");
        let mut called = vec![];
        render_with_engines(Path::new("office365.docx"), &target, |engine| {
            called.push(engine);
            fs::write(&target, b"%PDF-native")?;
            Ok(())
        })
        .unwrap();
        assert_eq!(
            called,
            if cfg!(windows) {
                vec![Engine::Word]
            } else {
                vec![Engine::Office]
            }
        );
    }

    #[test]
    fn fallback_discards_failed_and_invalid_outputs() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("result.pdf");
        let mut called = vec![];
        render_with_engines(Path::new("office365.docx"), &target, |engine| {
            called.push(engine);
            assert!(!target.exists());
            match engine {
                Engine::Word => {
                    fs::write(&target, b"%PDF-partial")?;
                    bail!("Export interrupted")
                }
                Engine::Office => {
                    fs::write(&target, b"invalid")?;
                    Ok(())
                }
                Engine::Local => {
                    fs::write(&target, b"%PDF-fallback")?;
                    Ok(())
                }
            }
        })
        .unwrap();
        let expected = if cfg!(windows) {
            vec![Engine::Word, Engine::Office, Engine::Local]
        } else {
            vec![Engine::Office, Engine::Local]
        };
        assert_eq!(called, expected);
        assert_eq!(fs::read(target).unwrap(), b"%PDF-fallback");
    }

    #[test]
    fn legacy_formats_never_use_html_and_preserve_engine_errors() {
        let dir = tempfile::tempdir().unwrap();
        let target = dir.path().join("result.pdf");
        let error = render_with_engines(Path::new("legacy.doc"), &target, |engine| {
            assert_ne!(engine, Engine::Local);
            bail!("native engine unavailable")
        })
        .unwrap_err();
        assert!(error.to_string().contains("native engine unavailable"));
    }
}
