use crate::{
    conversion, lists,
    settings::Settings,
    storage::{self, Lease},
    tags,
};
use anyhow::{Context, Result, bail};
use std::{
    collections::{BTreeMap, HashMap, HashSet},
    fs,
    path::{Path, PathBuf},
    time::{Duration, SystemTime},
};

pub const EXTENSIONS: &[&str] = &[
    "docx", "doc", "docm", "dotx", "dot", "dotm", "odt", "ott", "fodt", "rtf", "sxw", "stw",
];
pub fn extension(p: &Path) -> String {
    p.extension()
        .unwrap_or_default()
        .to_string_lossy()
        .to_lowercase()
}
pub fn stem(p: &Path) -> String {
    p.file_stem()
        .unwrap_or_default()
        .to_string_lossy()
        .into_owned()
}
pub fn supported(p: &Path) -> bool {
    EXTENSIONS.contains(&extension(p).as_str())
        && !p
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .starts_with("~$")
}
pub fn scan(folder: &Path, recursive: bool) -> Result<Vec<PathBuf>> {
    // read_dir fails on an unavailable NAS: never interpret this as an empty library.
    fs::read_dir(folder).with_context(|| {
        format!(
            "Dossier indisponible / Folder unavailable: {}",
            folder.display()
        )
    })?;
    let mut paths = vec![];
    for entry in walkdir::WalkDir::new(folder)
        .follow_links(false)
        .max_depth(if recursive { usize::MAX } else { 1 })
    {
        let e = entry?;
        if e.file_type().is_file() && supported(e.path()) {
            paths.push(e.into_path());
        }
    }
    paths.sort_by_key(|p| p.to_string_lossy().to_lowercase());
    Ok(paths)
}
#[derive(Clone, Debug)]
pub struct Document {
    pub name: String,
    pub source: PathBuf,
    pub pdf: Option<PathBuf>,
    pub modified: SystemTime,
    pub pdf_modified: Option<SystemTime>,
    pub size: u64,
    pub current: bool,
}
impl Document {
    pub fn status(&self) -> &'static str {
        if self.current {
            "synchronized"
        } else if self.pdf.is_some() {
            "pdf-outdated"
        } else {
            "pdf-missing"
        }
    }
}
pub fn library(s: &Settings) -> Result<Vec<Document>> {
    let mut by_name: BTreeMap<String, PathBuf> = BTreeMap::new();
    for path in scan(&s.documents, false)? {
        let key = stem(&path).to_lowercase();
        let preference = |p: &Path| {
            EXTENSIONS
                .iter()
                .position(|e| *e == extension(p))
                .unwrap_or(100)
        };
        if by_name
            .get(&key)
            .is_none_or(|p| preference(&path) < preference(p))
        {
            by_name.insert(key, path);
        }
    }
    let pdfs: HashMap<String, PathBuf> = fs::read_dir(&s.pdf)?
        .map(|e| e.map(|e| e.path()))
        .collect::<std::io::Result<Vec<_>>>()?
        .into_iter()
        .filter(|p| p.is_file() && extension(p) == "pdf")
        .map(|p| (stem(&p).to_lowercase(), p))
        .collect();
    let mut result = vec![];
    for (key, source) in by_name {
        let meta = source.metadata()?;
        let modified = meta.modified()?;
        let pdf = pdfs.get(&key).cloned();
        let pdf_meta = pdf.as_ref().and_then(|p| p.metadata().ok());
        let pdf_modified = pdf_meta.as_ref().and_then(|m| m.modified().ok());
        let current = pdf_meta.as_ref().is_some_and(|m| m.len() > 0)
            && pdf_modified.is_some_and(|t| t >= modified);
        result.push(Document {
            name: stem(&source),
            source,
            pdf,
            modified,
            pdf_modified,
            size: meta.len(),
            current,
        });
    }
    tags::unique(result.iter().map(|d| d.name.as_str()))?;
    Ok(result)
}
pub fn snapshot(s: &Settings) -> Result<String> {
    let mut parts = vec![];
    for (folder, recurse) in [(&s.documents, false), (&s.archive, true), (&s.pdf, false)] {
        fs::read_dir(folder)?;
        for entry in walkdir::WalkDir::new(folder).max_depth(if recurse { usize::MAX } else { 1 }) {
            let e = entry?;
            if !e.file_type().is_file() || !(supported(e.path()) || extension(e.path()) == "pdf") {
                continue;
            }
            let m = e.metadata()?;
            parts.push(format!(
                "{}|{}|{:?}",
                e.path().display(),
                m.len(),
                m.modified()?
            ));
        }
    }
    parts.sort();
    Ok(parts.join("\n"))
}
pub fn indexes(s: &Settings) -> Result<usize> {
    write_indexes(s, true)
}
pub fn indexes_preserving_lists(s: &Settings) -> Result<usize> {
    write_indexes(s, false)
}
fn write_indexes(s: &Settings, synchronize_lists: bool) -> Result<usize> {
    s.check_dirs()?;
    let docs = library(s)?;
    let archive = scan(&s.archive, true)?;
    let mut xml = format!(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<DocumentIndex version=\"1\" generatedAtUtc=\"{}\" rootPath=\"{}\" count=\"{}\">\n",
        storage::now(),
        storage::xml(s.root.to_string_lossy()),
        docs.len()
    );
    for d in &docs {
        xml.push_str(&format!(
            "  <Document name=\"{}\" status=\"{}\"{}>\n",
            storage::xml(&d.name),
            d.status(),
            lists::code_attr(&d.name)
        ));
        xml.push_str(&file_xml("EditableFile", &d.source, &s.data, None)?);
        if let Some(pdf) = &d.pdf {
            xml.push_str(&file_xml("PdfFile", pdf, &s.data, None)?);
        }
        xml.push_str("  </Document>\n");
    }
    xml.push_str("</DocumentIndex>\n");
    let mut ax = format!(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ArchiveIndex version=\"1\" generatedAtUtc=\"{}\" archivePath=\"{}\" count=\"{}\">\n",
        storage::now(),
        storage::xml(s.archive.to_string_lossy()),
        archive.len()
    );
    for p in archive {
        ax.push_str(&format!(
            "  <Document name=\"{}\"{}>\n{}  </Document>\n",
            storage::xml(stem(&p)),
            lists::code_attr(&stem(&p)),
            file_xml("EditableFile", &p, &s.data, Some(&s.archive))?
        ));
    }
    ax.push_str("</ArchiveIndex>\n");
    storage::atomic_write(&s.data.join("Index.xml"), xml.as_bytes())?;
    storage::atomic_write(&s.data.join("Archive.xml"), ax.as_bytes())?;
    let list_path = s.data.join("Lists.xml");
    let mut all = lists::load(&list_path)?;
    if synchronize_lists
        && lists::synchronize(
            &mut all,
            &docs.iter().map(|d| d.name.clone()).collect::<Vec<_>>(),
            &HashMap::new(),
        )?
    {
        lists::save(&list_path, &all)?;
    }
    Ok(docs.len())
}
fn file_xml(tag: &str, p: &Path, base: &Path, archive: Option<&Path>) -> Result<String> {
    let m = p.metadata()?;
    let date = |t: SystemTime| {
        chrono::DateTime::<chrono::Utc>::from(t).to_rfc3339_opts(chrono::SecondsFormat::Nanos, true)
    };
    let rel = |b: &Path| {
        storage::xml(
            pathdiff::diff_paths(p, b)
                .unwrap_or_else(|| p.into())
                .to_string_lossy(),
        )
    };
    Ok(format!(
        "    <{tag} name=\"{}\" relativePath=\"{}\"{} uri=\"{}\" format=\"{}\" sizeBytes=\"{}\" addedAtUtc=\"{}\" modifiedAtUtc=\"{}\" />\n",
        storage::xml(p.file_name().unwrap_or_default().to_string_lossy()),
        rel(base),
        archive
            .map(|a| format!(" archiveRelativePath=\"{}\"", rel(a)))
            .unwrap_or_default(),
        storage::xml(
            url::Url::from_file_path(p)
                .map_err(|_| anyhow::anyhow!("Invalid file URI"))?
                .as_str()
        ),
        extension(p),
        m.len(),
        date(m.created().unwrap_or(m.modified()?)),
        date(m.modified()?)
    ))
}
pub fn rename(s: &Settings, log: &mut impl FnMut(String)) -> Result<()> {
    let files = scan(&s.documents, false)?;
    tags::unique(
        files
            .iter()
            .map(|p| p.file_stem().unwrap().to_str().unwrap_or("")),
    )?;
    let prefix = tags::prefix(&s.prefix)?;
    let mut used: HashSet<u32> = files
        .iter()
        .filter_map(|p| tags::number(&stem(p), &prefix))
        .collect();
    let mut next = 1;
    for p in files {
        if tags::code(&stem(&p)).is_some() {
            continue;
        }
        while used.contains(&next) && next <= 99999 {
            next += 1;
        }
        if next > 99999 {
            bail!("Aucun numéro disponible / No number available");
        }
        let new = p.with_file_name(format!(
            "{prefix}-{next:05} {}",
            p.file_name().unwrap().to_string_lossy()
        ));
        // Use the same paired operation as manual renaming: otherwise the old
        // PDF becomes an orphan and the next full update deletes it.
        rename_procedure(s, &p, &stem(&new))?;
        used.insert(next);
        log(format!(
            "{} → {}",
            p.file_name().unwrap().to_string_lossy(),
            new.file_name().unwrap().to_string_lossy()
        ));
    }
    Ok(())
}
pub fn convert(s: &Settings, log: &mut impl FnMut(String)) -> Result<()> {
    convert_pdfs(s, false, log)
}

fn convert_pdfs(s: &Settings, force: bool, log: &mut impl FnMut(String)) -> Result<()> {
    let files = scan(&s.documents, false)?;
    let mut names = HashSet::new();
    for f in &files {
        if !names.insert(stem(f).to_lowercase()) {
            bail!(
                "Plusieurs documents produiraient le même PDF / Duplicate PDF target: {}",
                stem(f)
            );
        }
    }
    let docs = library(s)?;
    // Only remove orphan PDFs after both directories have been read successfully.
    for entry in fs::read_dir(&s.pdf)? {
        let p = entry?.path();
        if p.is_file() && extension(&p) == "pdf" && !names.contains(&stem(&p).to_lowercase()) {
            fs::remove_file(&p)?;
            log(format!(
                "PDF orphelin supprimé / Orphan PDF removed: {}",
                p.display()
            ));
        }
    }
    let mut errors = vec![];
    for d in docs.iter().filter(|d| force || !d.current) {
        let output = s.pdf.join(format!("{}.pdf", d.name));
        match conversion::convert(&d.source, &output) {
            Ok(()) => log(format!("PDF : {}", d.name)),
            Err(e) => {
                let msg = format!("{}: {e:#}", d.name);
                log(msg.clone());
                errors.push(msg);
            }
        }
    }
    if !errors.is_empty() {
        bail!("{}", errors.join("\n"));
    }
    Ok(())
}
#[derive(Clone)]
pub enum Action {
    Full,
    Tags,
    Convert,
    Index,
    Import(Vec<PathBuf>),
    Archive(Vec<PathBuf>),
    Rename { source: PathBuf, name: String },
}
pub fn run(s: &Settings, action: Action, automatic: bool, log: impl FnMut(String)) -> Result<()> {
    run_at(s, &s.root, action, automatic, log)
}
pub fn run_at(
    s: &Settings,
    locator: &Path,
    action: Action,
    automatic: bool,
    mut log: impl FnMut(String),
) -> Result<()> {
    s.validate()?;
    s.check_dirs()?;
    let _lease = Lease::existing(
        &s.data,
        Duration::from_millis(if automatic { 100 } else { 30000 }),
    )?;
    s.check_current(locator)?;
    // Detect disconnected shares before any mutation.
    for p in [&s.documents, &s.pdf, &s.archive] {
        fs::read_dir(p)?;
    }
    let full = matches!(action, Action::Full | Action::Import(_));
    match &action {
        Action::Rename { source, name } => {
            rename_procedure(s, source, name)?;
            log(format!(
                "Renommage / Renamed: {} → {}",
                stem(source),
                name.trim()
            ));
        }
        Action::Import(paths) => {
            for source in paths {
                if !supported(source) {
                    bail!(
                        "Format non pris en charge / Unsupported file: {}",
                        source.display()
                    );
                }
                let destination = s
                    .documents
                    .join(source.file_name().context("Invalid filename")?);
                if source == &destination {
                    continue;
                }
                copy_new(source, &destination)?;
                log(format!("Ajout / Added: {}", destination.display()));
            }
        }
        Action::Archive(paths) => {
            archive_documents(s, paths)?;
            for source in paths {
                log(format!("Archive : {}", source.display()));
            }
        }
        _ => {}
    }
    if full || matches!(action, Action::Tags) {
        rename(s, &mut log)?;
    }
    let conversion_result = if full || matches!(action, Action::Convert) {
        // A manual PDF conversion also repairs existing PDFs produced by older engines.
        convert_pdfs(s, matches!(action, Action::Convert), &mut log)
    } else {
        Ok(())
    };
    let count = indexes(s)?;
    storage::publish(&s.data, "workspace-update")?;
    log(format!("Index XML : {count}"));
    conversion_result
}
pub fn validate_procedure_name(name: &str) -> Result<()> {
    let name = name.trim();
    let device = name.split('.').next().unwrap_or_default().to_uppercase();
    if name.is_empty()
        || name.ends_with('.')
        || name.starts_with("~$")
        || name
            .chars()
            .any(|c| c.is_control() || "<>:\"/\\|?*".contains(c))
        || name.encode_utf16().count() > 240
        || matches!(
            device.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
        )
        || ["COM", "LPT"].iter().any(|prefix| {
            device.strip_prefix(prefix).is_some_and(|n| {
                matches!(
                    n,
                    "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
                )
            })
        })
    {
        bail!("Nom de procédure invalide / Invalid procedure name");
    }
    Ok(())
}

// Caller holds the workspace lease. Preserve file contents, extensions and dates.
fn rename_procedure(s: &Settings, source: &Path, name: &str) -> Result<()> {
    validate_procedure_name(name)?;
    let name = name.trim();
    let old_name = stem(source);
    let files = scan(&s.documents, false)?;
    if !files.iter().any(|p| p == source) {
        bail!("Sélection périmée : rechargez la bibliothèque / Stale selection: reload library");
    }
    if files
        .iter()
        .filter(|p| stem(p).to_lowercase() == old_name.to_lowercase())
        .count()
        != 1
    {
        bail!("Plusieurs documents portent ce nom / Multiple source documents have this name");
    }
    if name == old_name {
        return Ok(());
    }
    let other_names: Vec<String> = files
        .iter()
        .filter(|p| *p != source)
        .map(|p| stem(p))
        .collect();
    if other_names
        .iter()
        .any(|n| n.to_lowercase() == name.to_lowercase())
    {
        bail!("Une procédure porte déjà ce nom / Procedure name already exists");
    }
    tags::unique(
        other_names
            .iter()
            .map(String::as_str)
            .chain(std::iter::once(name)),
    )?;
    let target = source.with_file_name(format!(
        "{}.{}",
        name,
        source.extension().unwrap().to_string_lossy()
    ));
    let mut moves = vec![(source.to_owned(), target)];
    for entry in fs::read_dir(&s.pdf)? {
        let pdf = entry?.path();
        if extension(&pdf) != "pdf" {
            continue;
        }
        let pdf_name = stem(&pdf).to_lowercase();
        if pdf_name == old_name.to_lowercase() {
            if !pdf.is_file() || moves.len() > 1 {
                bail!("PDF ambigu ou inaccessible / Ambiguous or inaccessible PDF");
            }
            let target = pdf.with_file_name(format!(
                "{}.{}",
                name,
                pdf.extension().unwrap().to_string_lossy()
            ));
            moves.push((pdf, target));
        } else if pdf_name == name.to_lowercase() {
            bail!("Un PDF porte déjà ce nom / PDF name already exists");
        }
    }
    for (from, to) in &moves {
        if to.try_exists()? && fs::canonicalize(from)? != fs::canonicalize(to)? {
            bail!(
                "Destination existante / Destination exists: {}",
                to.display()
            );
        }
    }
    let list_path = s.data.join("Lists.xml");
    let mut all = lists::load(&list_path)?;
    let mut lists_changed = false;
    let old_code = tags::code(&old_name);
    for list in &mut all {
        let mut changed = false;
        for stored in &mut list.procedures {
            if stored.to_lowercase() == old_name.to_lowercase()
                || old_code
                    .as_ref()
                    .is_some_and(|code| tags::code(stored).as_ref() == Some(code))
            {
                *stored = name.to_owned();
                changed = true;
            }
        }
        if changed {
            list.modified = storage::now();
            lists_changed = true;
        }
    }
    let mut completed = 0;
    let result: Result<()> = (|| {
        for (from, to) in &moves {
            fs::rename(from, to)
                .with_context(|| format!("Renommage / Rename: {}", from.display()))?;
            completed += 1;
        }
        if lists_changed {
            lists::save(&list_path, &all)?;
        }
        Ok(())
    })();
    if let Err(error) = result {
        let mut rollback_errors = vec![];
        for (from, to) in moves[..completed].iter().rev() {
            if let Err(e) = fs::rename(to, from) {
                rollback_errors.push(format!("{} → {}: {e}", to.display(), from.display()));
            }
        }
        if !rollback_errors.is_empty() {
            bail!(
                "{error:#}\nRestauration incomplète / Incomplete rollback: {}",
                rollback_errors.join("; ")
            );
        }
        return Err(error);
    }
    Ok(())
}

fn copy_new(source: &Path, target: &Path) -> Result<fs::Metadata> {
    let mut src = fs::File::open(source)?;
    let before = src.metadata()?;
    let mut dest = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(target)
        .with_context(|| {
            format!(
                "Fichier déjà présent ou inaccessible / Existing or inaccessible file: {}",
                target.display()
            )
        })?;
    if let Err(e) = std::io::copy(&mut src, &mut dest)
        .and_then(|_| dest.set_times(fs::FileTimes::new().set_modified(before.modified()?)))
        .and_then(|_| dest.sync_all())
    {
        drop(dest);
        let _ = fs::remove_file(target);
        return Err(e.into());
    }
    let after = src.metadata()?;
    if before.len() != after.len() || before.modified()? != after.modified()? {
        drop(dest);
        let _ = fs::remove_file(target);
        bail!("Fichier modifié pendant la copie / File changed while copying");
    }
    Ok(before)
}
fn archive_documents(s: &Settings, paths: &[PathBuf]) -> Result<()> {
    let docs = library(s)?;
    let sources = scan(&s.documents, false)?;
    // Fail before moving anything if the lists cannot even be read.
    lists::load(&s.data.join("Lists.xml"))?;
    let mut reserved: HashSet<String> = fs::read_dir(&s.archive)?
        .map(|entry| entry.map(|e| stem(&e.path()).to_lowercase()))
        .collect::<std::io::Result<_>>()?;
    let mut selected = HashSet::new();
    let mut moves = vec![];
    for source in paths {
        if !selected.insert(source) {
            continue;
        }
        let doc = docs
            .iter()
            .find(|d| &d.source == source)
            .context("Sélection périmée / Stale selection")?;
        if sources
            .iter()
            .filter(|p| stem(p).to_lowercase() == doc.name.to_lowercase())
            .count()
            != 1
        {
            bail!("Plusieurs documents portent ce nom / Multiple source documents have this name");
        }
        let mut name = doc.name.clone();
        while !reserved.insert(name.to_lowercase()) {
            name = format!("{} {}", doc.name, uuid::Uuid::new_v4().simple());
        }
        // One common suffix for the pair, including when only the PDF collides.
        for from in std::iter::once(&doc.source).chain(doc.pdf.iter()) {
            let to = s.archive.join(format!(
                "{}.{}",
                name,
                from.extension().unwrap().to_string_lossy()
            ));
            moves.push((from.clone(), to));
        }
    }
    // Stage every copy before deleting any source, including across volumes/NAS.
    // If a later deletion fails (e.g. Word or a PDF reader locks a file), restore
    // deleted sources before cleaning the staged copies.
    let mut copied = 0;
    let mut removed = 0;
    let mut source_versions = vec![];
    let result: Result<()> = (|| {
        for (from, to) in &moves {
            source_versions.push(copy_new(from, to)?);
            copied += 1;
        }
        // Compare source timestamps to their originals, since another filesystem
        // can round the timestamps on archive copies (e.g. removable drives).
        for ((from, _), original) in moves.iter().zip(&source_versions) {
            let current = from.metadata()?;
            if current.len() != original.len() || current.modified()? != original.modified()? {
                bail!(
                    "Fichier modifié pendant l’archivage / File changed during archival: {}",
                    from.display()
                );
            }
        }
        for (from, _) in &moves {
            fs::remove_file(from)
                .with_context(|| format!("Archivage / Archive: {}", from.display()))?;
            removed += 1;
        }
        Ok(())
    })();
    if let Err(error) = result {
        let mut rollback_errors = vec![];
        for (i, (from, to)) in moves[..copied].iter().enumerate().rev() {
            if i < removed
                && let Err(e) = copy_new(to, from)
            {
                // Never remove the recovery copy if restoration failed.
                rollback_errors.push(format!("{} → {}: {e:#}", to.display(), from.display()));
                continue;
            }
            if let Err(e) = fs::remove_file(to) {
                rollback_errors.push(format!("{}: {e}", to.display()));
            }
        }
        if !rollback_errors.is_empty() {
            bail!(
                "{error:#}\nRestauration incomplète / Incomplete rollback: {}",
                rollback_errors.join("; ")
            );
        }
        return Err(error);
    }
    Ok(())
}
