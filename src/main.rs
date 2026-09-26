#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]
mod ui;
mod worker;
use anyhow::{Context, Result, bail};
use procedure_pilot::{
    conversion,
    settings::Settings,
    storage,
    workspace::{self, Action},
};
use std::path::PathBuf;

fn arg(args: &[String], name: &str) -> Option<String> {
    args.windows(2)
        .find(|w| w[0].eq_ignore_ascii_case(name))
        .map(|w| w[1].clone())
}
fn main() {
    let args: Vec<String> = std::env::args().collect();
    if let Err(e) = run(&args) {
        let text = format!("{e:#}");
        if let Some(path) = arg(&args, "--report") {
            let _ = std::fs::write(path, format!("ERROR\n{text}"));
        } else if !args.iter().any(|a| a == "--convert-word") {
            rfd::MessageDialog::new()
                .set_title("Procedure Pilot")
                .set_description(&text)
                .set_level(rfd::MessageLevel::Error)
                .show();
        }
        std::process::exit(1);
    }
}
fn run(args: &[String]) -> Result<()> {
    if let Some(input) = arg(args, "--convert-word") {
        let output = arg(args, "--output").context("--output required")?;
        #[cfg(windows)]
        return conversion::word(&PathBuf::from(input), &PathBuf::from(output));
        #[cfg(not(windows))]
        {
            let _ = (input, output);
            bail!("Word is Windows-only");
        }
    }
    let executable = std::env::current_exe()?;
    let folder = executable
        .parent()
        .context("Executable directory unavailable")?;
    let root = if let Some(root) = arg(args, "--root") {
        std::path::absolute(root)?
    } else if !folder.join(".procedurepilot-recovery.xml").exists()
        && !folder.join(".procedurepilot-data-folder").exists()
        && !folder.join("Data/.procedurepilot-recovery.xml").exists()
        && !folder.join("Data/.procedurepilot-data-folder").exists()
        && !folder.join("Data/Settings.xml").exists()
        && !folder.join("Data/ProcedurePilot.data-folder").exists()
        && !folder.join("Documents").exists()
        && folder.parent().is_some_and(|p| {
            p.join("Documents").exists() || p.join("Procedures_Modifiables").exists()
        })
    {
        folder.parent().unwrap().to_owned()
    } else {
        folder.to_owned()
    };
    if let Some(input) = arg(args, "--convert-local") {
        let output = std::path::absolute(arg(args, "--output").context("--output required")?)?;
        if !conversion::local_supported(&PathBuf::from(&input)) {
            bail!("OOXML document required");
        }
        std::fs::create_dir_all(output.parent().context("Missing output folder")?)?;
        let temp = tempfile::tempdir_in(output.parent().unwrap())?;
        let generated = temp.path().join("document.pdf");
        conversion::local(&std::path::absolute(input)?, &generated)?;
        return storage::atomic_write(&output, &std::fs::read(generated)?);
    }
    if let Some(input) = arg(args, "--render-html") {
        let output = arg(args, "--output").context("--output required")?;
        std::fs::write(
            output,
            procedure_pilot::docx::render(&PathBuf::from(input))?,
        )?;
        return Ok(());
    }
    let cli = args.iter().any(|a| {
        matches!(
            a.as_str(),
            "--self-test"
                | "--test-xml-index"
                | "--test-portable-index"
                | "--index"
                | "--sync"
                | "--convert-pdfs"
                | "--tags"
        )
    });
    let s = if cli {
        Settings::initialize(&root)?
    } else {
        Settings::discover(&root)?.0
    };
    if args.iter().any(|a| a == "--self-test") {
        let report = format!(
            "Procedure Pilot Rust — autotest\nRoot={}\nDocumentsPath={}\nPdfPath={}\nArchivePath={}\nDataPath={}\nDocumentCount={}\nLanguage={}\nTagPrefix={}\nLocalPdfEngineAvailable={}\nLibreOfficeOrOpenOfficeAvailable={}\nIndexFormat=XML\n",
            s.root.display(),
            s.documents.display(),
            s.pdf.display(),
            s.archive.display(),
            s.data.display(),
            workspace::scan(&s.documents, false)?.len(),
            s.language,
            s.prefix,
            conversion::browser().is_some(),
            conversion::soffice().is_some()
        );
        if let Some(path) = arg(args, "--report") {
            std::fs::write(path, report)?;
        }
        return Ok(());
    }
    let command = if args.iter().any(|a| {
        matches!(
            a.as_str(),
            "--test-xml-index" | "--test-portable-index" | "--index"
        )
    }) {
        Some(Action::Index)
    } else if args.iter().any(|a| a == "--convert-pdfs") {
        Some(Action::Convert)
    } else if args.iter().any(|a| a == "--sync") {
        Some(Action::Full)
    } else if args.iter().any(|a| a == "--tags") {
        Some(Action::Tags)
    } else {
        None
    };
    if let Some(command) = command {
        workspace::run_at(&s, &root, command, false, |line| {
            let _ = storage::log(&s.data, true, &line);
        })?;
        if let Some(path) = arg(args, "--report") {
            std::fs::write(path, "OK\n")?;
        }
        return Ok(());
    }
    let screenshot = arg(args, "--screenshot").map(PathBuf::from);
    // Optional dimensions for visual verification; normal launches keep the default size.
    let mut window_size = [1220.0, 790.0];
    if screenshot.is_some()
        && let Some(size) = arg(args, "--screenshot-size")
    {
        let (width, height) = size.split_once('x').context("Expected WIDTHxHEIGHT")?;
        let width: u32 = width.parse()?;
        let height: u32 = height.parse()?;
        if !(1040..=3840).contains(&width) || !(680..=2160).contains(&height) {
            bail!("Screenshot size outside supported window dimensions");
        }
        window_size = [width as f32, height as f32];
    }
    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_icon(eframe::icon_data::from_png_bytes(include_bytes!(concat!(
                env!("OUT_DIR"),
                "/app-icon.png"
            )))?)
            .with_inner_size(window_size)
            .with_min_inner_size([1040.0, 680.0]),
        ..Default::default()
    };
    eframe::run_native(
        "Procedure Pilot",
        options,
        Box::new(move |cc| Ok(Box::new(ui::Pilot::new(cc, root, s, screenshot)))),
    )
    .map_err(|e| anyhow::anyhow!("{e}"))
}
