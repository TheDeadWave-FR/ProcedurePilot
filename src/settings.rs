use crate::{
    storage::{self, Lease},
    tags,
};
use anyhow::{Context, Result, bail};
use std::{
    fs,
    path::{Path, PathBuf},
    time::Duration,
};

#[derive(Clone, Debug, PartialEq)]
pub struct Settings {
    pub root: PathBuf,
    pub documents: PathBuf,
    pub pdf: PathBuf,
    pub archive: PathBuf,
    pub data: PathBuf,
    pub language: String,
    pub prefix: String,
}
impl Settings {
    pub fn defaults(root: &Path) -> Self {
        Self {
            root: root.into(),
            documents: root.join("Documents"),
            pdf: root.join("PDFs"),
            archive: root.join("Archive"),
            data: root.join("Data"),
            language: "fr".into(),
            prefix: "FR".into(),
        }
    }
    pub fn resolve(root: &Path) -> Result<PathBuf> {
        for loc in [
            root.join("Data/.procedurepilot-data-folder"),
            root.join(".procedurepilot-data-folder"),
            root.join("Data/ProcedurePilot.data-folder"),
            root.join("Settings/ProcedurePilot.settings-folder"),
        ] {
            if loc.try_exists()? {
                let p = PathBuf::from(storage::read(&loc)?.trim());
                for name in ["Settings.xml", "ProcedurePilot.settings.xml"] {
                    let file = p.join(name);
                    if file.try_exists()? {
                        return Ok(file);
                    }
                }
                // A disconnected custom data directory must never reset the workspace.
                bail!(
                    "Dossier de données indisponible / Data folder unavailable: {}",
                    p.display()
                );
            }
        }
        for file in [
            root.join("Data/Settings.xml"),
            root.join("Settings/ProcedurePilot.settings.xml"),
            root.join("ProcedurePilot.settings.xml"),
        ] {
            if file.try_exists()? {
                return Ok(file);
            }
        }
        Ok(root.join("Data/Settings.xml"))
    }
    pub fn load(root: &Path) -> Result<Self> {
        let path = Self::resolve(root)?;
        if !path.try_exists()? {
            return Ok(Self::defaults(root));
        }
        let text = storage::read(&path)?;
        Self::parse(root, &text)
    }
    fn parse(root: &Path, text: &str) -> Result<Self> {
        let doc = roxmltree::Document::parse(text).context(
            "Settings.xml illisible ; fichier conservé / Invalid Settings.xml; file preserved",
        )?;
        let node = doc.root_element();
        if !matches!(
            node.tag_name().name(),
            "ApplicationSettings" | "ProcedurePilotSettings"
        ) {
            bail!("Invalid settings root");
        }
        let val = |name| {
            node.children()
                .find(|n| n.has_tag_name(name))
                .and_then(|n| n.text())
                .map(str::trim)
                .filter(|s| !s.is_empty())
        };
        let base = val("RootPath")
            .map(PathBuf::from)
            .unwrap_or_else(|| root.into());
        let mut s = Self::defaults(&base);
        for (field, key) in [
            (&mut s.documents, "DocumentsPath"),
            (&mut s.pdf, "PdfPath"),
            (&mut s.archive, "ArchivePath"),
        ] {
            if let Some(value) = val(key) {
                *field = absolute(&base, Path::new(value));
            }
        }
        if let Some(value) = val("DataPath").or_else(|| val("SettingsFolderPath")) {
            s.data = absolute(&base, Path::new(value));
        }
        s.language = normalize_language(val("Language").unwrap_or("fr"));
        s.prefix = tags::prefix(val("TagPrefix").unwrap_or("FR"))?;
        Ok(s)
    }
    /// Read-only discovery: never creates folders or replaces unavailable settings.
    /// The boolean indicates that the primary settings file was found.
    pub fn discover(root: &Path) -> Result<(Self, bool)> {
        if let Ok(path) = Self::resolve(root)
            && path.try_exists()?
        {
            let s = Self::parse(root, &storage::read(&path)?)?;
            s.validate()?;
            return Ok((s, true));
        }
        for recovery in Self::recovery_paths(root) {
            if recovery.try_exists()? {
                let s = Self::parse(root, &storage::read(&recovery)?)?;
                s.validate()?;
                return Ok((s, false));
            }
        }
        let mut s = Self::defaults(root);
        for (path, legacy) in [
            (&mut s.documents, "Procedures_Modifiables"),
            (&mut s.pdf, "Procedures_PDF"),
            (&mut s.archive, "_ARCHIVE"),
            (&mut s.data, "Settings"),
        ] {
            if !path.try_exists()? && root.join(legacy).is_dir() {
                *path = root.join(legacy);
            }
        }
        for location in [
            root.join("Data/.procedurepilot-data-folder"),
            root.join(".procedurepilot-data-folder"),
            root.join("Data/ProcedurePilot.data-folder"),
            root.join("Settings/ProcedurePilot.settings-folder"),
        ] {
            if location.try_exists()? {
                s.data = PathBuf::from(storage::read(&location)?.trim());
                break;
            }
        }
        s.validate()?;
        Ok((s, false))
    }
    pub fn folders(&self) -> [&Path; 4] {
        [&self.documents, &self.pdf, &self.archive, &self.data]
    }
    pub fn unavailable(&self) -> Vec<(usize, String)> {
        self.folders()
            .into_iter()
            .enumerate()
            .filter_map(|(i, p)| fs::read_dir(p).err().map(|e| (i, e.to_string())))
            .collect()
    }
    pub fn check_dirs(&self) -> Result<()> {
        for p in self.folders() {
            fs::read_dir(p).with_context(|| {
                format!("Dossier indisponible / Folder unavailable: {}", p.display())
            })?;
        }
        Ok(())
    }
    /// Only the folders explicitly approved by the user may be created.
    pub fn install(&self, locator: &Path, original: &Self, create: [bool; 4]) -> Result<()> {
        self.validate()?;
        for (i, path) in self.folders().into_iter().enumerate() {
            if !create[i] || path.try_exists()? {
                fs::read_dir(path).with_context(|| {
                    format!("Sélectionnez un dossier accessible : {}", path.display())
                })?;
            }
        }
        let old_lease = if fs::read_dir(&original.data).is_ok() {
            Some(Lease::existing(&original.data, Duration::from_secs(10))?)
        } else {
            None
        };
        if Self::discover(locator)?.0 != *original {
            bail!(
                "Les paramètres ont changé. Relancez l’application / Settings changed: restart the application"
            );
        }
        for (i, path) in self.folders().into_iter().enumerate() {
            if create[i] {
                fs::create_dir_all(path)?;
            }
        }
        self.check_dirs()?;
        let _lease = if self.data != original.data || old_lease.is_none() {
            Some(Lease::existing(&self.data, Duration::from_secs(10))?)
        } else {
            None
        };
        if Self::discover(locator)?.0 != *original {
            bail!(
                "Les paramètres ont changé. Relancez l’application / Settings changed: restart the application"
            );
        }
        self.save()?;
        self.write_location(locator)?;
        Ok(())
    }
    pub fn initialize(root: &Path) -> Result<Self> {
        // Lock the old location before migrations, retaining the file handle until complete.
        let lock_dir = Self::resolve(root)?
            .parent()
            .context("Invalid settings path")?
            .to_owned();
        let _lease = Lease::acquire(&lock_dir, Duration::from_secs(30))?;
        let mut s = Self::load(root)?;
        for (path, old, new) in [
            (&mut s.documents, "Procedures_Modifiables", "Documents"),
            (&mut s.pdf, "Procedures_PDF", "PDFs"),
            (&mut s.archive, "_ARCHIVE", "Archive"),
        ] {
            migrate(path, &s.root, old, new)?;
        }
        // Keep an existing legacy data directory: its open SMB lock cannot be moved safely.
        if s.data == root.join("Data") && !s.data.exists() && root.join("Settings").exists() {
            s.data = root.join("Settings");
        }
        s.validate()?;
        let _target = if s.data != lock_dir {
            Some(Lease::acquire(&s.data, Duration::from_secs(30))?)
        } else {
            None
        };
        s.ensure_dirs()?;
        s.save()?;
        s.write_location(root)?;
        Ok(s)
    }
    pub fn validate(&self) -> Result<()> {
        tags::prefix(&self.prefix)?;
        let paths = [&self.documents, &self.pdf, &self.archive, &self.data];
        for (i, a) in paths.iter().enumerate() {
            if !a.is_absolute() {
                bail!(
                    "Chemin absolu requis / Absolute path required: {}",
                    a.display()
                );
            }
            for b in paths.iter().skip(i + 1) {
                let a = normalized(a);
                let b = normalized(b);
                if a == b || a.starts_with(&(b.clone() + "/")) || b.starts_with(&(a.clone() + "/"))
                {
                    bail!(
                        "Les dossiers doivent être distincts et non imbriqués / Folders must be distinct and not nested"
                    );
                }
            }
        }
        Ok(())
    }
    pub fn ensure_dirs(&self) -> Result<()> {
        for p in [&self.documents, &self.pdf, &self.archive, &self.data] {
            fs::create_dir_all(p)?;
        }
        Ok(())
    }
    pub fn save(&self) -> Result<()> {
        storage::atomic_write(&self.data.join("Settings.xml"), self.to_xml()?.as_bytes())
    }
    /// Caller holds the data lease; an instance must not write using obsolete paths/prefixes.
    pub fn check_current(&self, locator: &Path) -> Result<()> {
        let current = Self::load(locator)?;
        if current != *self {
            bail!(
                "Les paramètres ont changé. Réessayez après actualisation / Settings changed; retry after refresh"
            );
        }
        Ok(())
    }
    /// Refresh launcher metadata only while holding the lease for the current data directory.
    pub fn refresh_location(&self, root: &Path) -> Result<()> {
        let _lease = Lease::existing(&self.data, Duration::from_millis(100))?;
        self.check_current(root)?;
        self.write_location(root)
    }
    fn to_xml(&self) -> Result<String> {
        self.validate()?;
        let mut xml = String::from(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ApplicationSettings version=\"5\">\n",
        );
        for (key, value) in [
            ("RootPath", self.root.display().to_string()),
            ("DocumentsPath", self.documents.display().to_string()),
            ("PdfPath", self.pdf.display().to_string()),
            ("ArchivePath", self.archive.display().to_string()),
            ("DataPath", self.data.display().to_string()),
            ("Language", self.language.clone()),
            ("TagPrefix", tags::prefix(&self.prefix)?),
        ] {
            xml.push_str(&format!("  <{key}>{}</{key}>\n", storage::xml(value)));
        }
        xml.push_str("  <WatchFolders>true</WatchFolders>\n  <DebounceMilliseconds>1500</DebounceMilliseconds>\n</ApplicationSettings>\n");
        Ok(xml)
    }
    pub fn recovery_paths(root: &Path) -> [PathBuf; 2] {
        [
            root.join("Data/.procedurepilot-recovery.xml"),
            root.join(".procedurepilot-recovery.xml"),
        ]
    }
    pub fn save_recovery(&self, root: &Path) -> Result<()> {
        storage::atomic_write(
            &root.join("Data/.procedurepilot-recovery.xml"),
            self.to_xml()?.as_bytes(),
        )
    }
    pub fn write_location(&self, root: &Path) -> Result<()> {
        // Keep the launcher locator discoverable even with a custom data directory.
        fs::create_dir_all(root.join("Data"))?;
        storage::atomic_write(
            &root.join("Data/.procedurepilot-data-folder"),
            self.data.to_string_lossy().as_bytes(),
        )?;
        self.save_recovery(root)?;
        // Remove legacy copies only after both new files have been saved successfully.
        for name in [
            ".procedurepilot-data-folder",
            ".procedurepilot-recovery.xml",
        ] {
            match fs::remove_file(root.join(name)) {
                Ok(()) => {}
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                Err(e) => return Err(e.into()),
            }
        }
        Ok(())
    }
}
fn normalized(path: &Path) -> String {
    let mut lexical = PathBuf::new();
    for component in path.components() {
        match component {
            std::path::Component::CurDir => {}
            std::path::Component::ParentDir => {
                lexical.pop();
            }
            _ => lexical.push(component.as_os_str()),
        }
    }
    let p = fs::canonicalize(&lexical).unwrap_or(lexical);
    p.to_string_lossy()
        .replace('\\', "/")
        .trim_start_matches("//?/")
        .trim_end_matches('/')
        .to_lowercase()
}
fn migrate(configured: &mut PathBuf, root: &Path, old: &str, new: &str) -> Result<()> {
    let legacy = root.join(old);
    let current = root.join(new);
    if *configured == legacy || *configured == current {
        if legacy.try_exists()? && !current.try_exists()? {
            fs::rename(&legacy, &current)?;
        }
        if !legacy.try_exists()? {
            *configured = current;
        }
    }
    Ok(())
}
pub fn absolute(base: &Path, path: &Path) -> PathBuf {
    if path.is_absolute() {
        path.into()
    } else {
        base.join(path)
    }
}
pub fn normalize_language(s: &str) -> String {
    if s.to_ascii_lowercase().starts_with("en") {
        "en".into()
    } else {
        "fr".into()
    }
}
fn user_path() -> Option<PathBuf> {
    std::env::var_os("LOCALAPPDATA")
        .map(|p| PathBuf::from(p).join("ProcedurePilot/UserSettings.xml"))
}
pub fn user_language(fallback: &str) -> String {
    let result = (|| {
        let text = storage::read(&user_path()?).ok()?;
        let doc = roxmltree::Document::parse(&text).ok()?;
        Some(normalize_language(
            doc.root_element()
                .children()
                .find(|n| n.has_tag_name("Language"))?
                .text()?,
        ))
    })();
    result.unwrap_or_else(|| normalize_language(fallback))
}
pub fn save_user_language(language: &str) -> Result<()> {
    let path = user_path().context("LOCALAPPDATA unavailable")?;
    fs::create_dir_all(path.parent().context("Missing user settings directory")?)?;
    storage::atomic_write(
        &path,
        format!(
            "<UserSettings version=\"1\"><Language>{}</Language></UserSettings>",
            normalize_language(language)
        )
        .as_bytes(),
    )
}
