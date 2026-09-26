use procedure_pilot::{
    docx,
    lists::{self, ProcedureList},
    settings::Settings,
    storage::{self, Lease},
    workspace::{self, Action},
};
use std::{
    collections::HashMap,
    fs,
    io::Write,
    time::{Duration, SystemTime},
};

fn setup() -> (tempfile::TempDir, Settings) {
    let dir = tempfile::tempdir().unwrap();
    let s = Settings::initialize(dir.path()).unwrap();
    (dir, s)
}
fn put(s: &Settings, name: &str) {
    fs::write(s.documents.join(name), b"document").unwrap();
}
fn list(name: &str, procedures: &[&str]) -> ProcedureList {
    let mut l = ProcedureList::new();
    l.name = name.into();
    l.procedures = procedures.iter().map(|s| s.to_string()).collect();
    l
}

fn rename_procedure(s: &Settings, old: &str, new: &str) -> anyhow::Result<()> {
    workspace::run(
        s,
        Action::Rename {
            source: s.documents.join(old),
            name: new.into(),
        },
        false,
        |_| {},
    )
}

#[test]
fn procedure_rename_preserves_files_dates_and_list_order() {
    let (_dir, s) = setup();
    put(&s, "FR-00001 Avant.DOCX");
    put(&s, "FR-00002 Autre.docx");
    let pdf = s.pdf.join("FR-00001 Avant.PDF");
    fs::write(&pdf, b"%PDF-1.7 original").unwrap();
    let source_date = s
        .documents
        .join("FR-00001 Avant.DOCX")
        .metadata()
        .unwrap()
        .modified()
        .unwrap();
    let pdf_date = pdf.metadata().unwrap().modified().unwrap();
    lists::save(
        &s.data.join("Lists.xml"),
        &[list("A", &["FR-00002 Autre", "FR-00001 Avant"])],
    )
    .unwrap();
    rename_procedure(&s, "FR-00001 Avant.DOCX", "FR-00003 Après & suite").unwrap();
    let new_source = s.documents.join("FR-00003 Après & suite.DOCX");
    let new_pdf = s.pdf.join("FR-00003 Après & suite.PDF");
    assert_eq!(fs::read(&new_source).unwrap(), b"document");
    assert_eq!(fs::read(&new_pdf).unwrap(), b"%PDF-1.7 original");
    assert_eq!(
        new_source.metadata().unwrap().modified().unwrap(),
        source_date
    );
    assert_eq!(new_pdf.metadata().unwrap().modified().unwrap(), pdf_date);
    assert!(!pdf.exists());
    assert!(!s.documents.join("FR-00001 Avant.DOCX").exists());
    assert_eq!(
        lists::load(&s.data.join("Lists.xml")).unwrap()[0].procedures,
        ["FR-00002 Autre", "FR-00003 Après & suite"]
    );
    let index = storage::read(&s.data.join("Index.xml")).unwrap();
    assert!(index.contains("FR-00003 Après &amp; suite.PDF"));
    assert!(!index.contains("FR-00001 Avant"));
}

#[test]
fn procedure_rename_without_pdf_updates_untagged_list_references() {
    let (_dir, s) = setup();
    put(&s, "Avant.odt");
    lists::save(&s.data.join("Lists.xml"), &[list("A", &["Avant"])]).unwrap();
    rename_procedure(&s, "Avant.odt", "Après").unwrap();
    assert!(s.documents.join("Après.odt").exists());
    assert_eq!(fs::read_dir(&s.pdf).unwrap().count(), 0);
    assert_eq!(
        lists::load(&s.data.join("Lists.xml")).unwrap()[0].procedures,
        ["Après"]
    );
}

#[test]
fn procedure_rename_rejects_collisions_invalid_names_and_stale_sources() {
    let (_dir, s) = setup();
    put(&s, "FR-00001 Avant.docx");
    put(&s, "FR-00002 Autre.odt");
    fs::write(s.pdf.join("FR-00001 Avant.pdf"), b"old pdf").unwrap();
    fs::write(s.pdf.join("Reserve.pdf"), b"reserved pdf").unwrap();
    for name in [
        "",
        "../hors-dossier",
        "a/b",
        "a\\b",
        "a:b",
        "a?b",
        "a*b",
        "a\"b",
        "a<b",
        "a>b",
        "a|b",
        "a\nb",
        "fin.",
        "NUL",
        "con.txt",
        "COM1",
        "LPT¹",
        "~$temp",
        "fr-00002 autre",
        "FR-00002 Doublon",
        "reserve",
    ] {
        assert!(
            rename_procedure(&s, "FR-00001 Avant.docx", name).is_err(),
            "{name}"
        );
        assert!(s.documents.join("FR-00001 Avant.docx").exists());
        assert_eq!(
            fs::read(s.pdf.join("FR-00001 Avant.pdf")).unwrap(),
            b"old pdf"
        );
    }
    assert!(rename_procedure(&s, "Absent.docx", "Après").is_err());
    assert_eq!(
        fs::read(s.pdf.join("Reserve.pdf")).unwrap(),
        b"reserved pdf"
    );
}

#[test]
fn procedure_rename_handles_case_only_changes() {
    let (_dir, s) = setup();
    put(&s, "Avant.docx");
    fs::write(s.pdf.join("Avant.pdf"), b"pdf").unwrap();
    rename_procedure(&s, "Avant.docx", "AVANT").unwrap();
    let docs = workspace::library(&s).unwrap();
    assert_eq!(docs[0].name, "AVANT");
    assert_eq!(
        docs[0].pdf.as_ref().unwrap().file_name().unwrap(),
        "AVANT.pdf"
    );
}

#[test]
fn procedure_rename_rejects_ambiguous_sources_and_corrupt_lists_before_moving() {
    let (_dir, s) = setup();
    put(&s, "Avant.docx");
    put(&s, "Avant.odt");
    assert!(rename_procedure(&s, "Avant.docx", "Après").is_err());
    fs::remove_file(s.documents.join("Avant.odt")).unwrap();
    fs::write(s.data.join("Lists.xml"), b"not XML").unwrap();
    assert!(rename_procedure(&s, "Avant.docx", "Après").is_err());
    assert!(s.documents.join("Avant.docx").exists());
    assert!(!s.documents.join("Après.docx").exists());
}

#[cfg(windows)]
#[test]
fn procedure_rename_rolls_back_when_pdf_or_list_is_locked() {
    use std::os::windows::fs::OpenOptionsExt;
    for lock_lists in [false, true] {
        let (_dir, s) = setup();
        put(&s, "Avant.docx");
        let pdf = s.pdf.join("Avant.pdf");
        let list_path = s.data.join("Lists.xml");
        fs::write(&pdf, b"original pdf").unwrap();
        lists::save(&list_path, &[list("A", &["Avant"])]).unwrap();
        let original_lists = fs::read(&list_path).unwrap();
        let locked = if lock_lists { &list_path } else { &pdf };
        // Allow reads but deny delete/rename and replacement while the handle lives.
        let _handle = fs::OpenOptions::new()
            .read(true)
            .share_mode(1)
            .open(locked)
            .unwrap();
        assert!(rename_procedure(&s, "Avant.docx", "Après").is_err());
        assert_eq!(
            fs::read(s.documents.join("Avant.docx")).unwrap(),
            b"document"
        );
        assert_eq!(fs::read(&pdf).unwrap(), b"original pdf");
        assert_eq!(fs::read(&list_path).unwrap(), original_lists);
        assert!(!s.documents.join("Après.docx").exists());
        assert!(!s.pdf.join("Après.pdf").exists());
    }
}

#[test]
fn settings_roundtrip_and_legacy_xml() {
    let (dir, mut s) = setup();
    s.prefix = "IT-SUP".into();
    s.language = "en".into();
    s.save().unwrap();
    assert_eq!(Settings::load(dir.path()).unwrap(), s);
    let text = fs::read_to_string(s.data.join("Settings.xml"))
        .unwrap()
        .replace("ApplicationSettings", "ProcedurePilotSettings")
        .replace("DataPath", "SettingsFolderPath");
    fs::write(s.data.join("Settings.xml"), format!("\u{feff}{text}")).unwrap();
    assert_eq!(Settings::load(dir.path()).unwrap(), s);
}
#[test]
fn corrupt_settings_are_preserved() {
    let (dir, s) = setup();
    let p = s.data.join("Settings.xml");
    fs::write(&p, "<broken>").unwrap();
    assert!(Settings::initialize(dir.path()).is_err());
    assert_eq!(fs::read_to_string(p).unwrap(), "<broken>");
}
#[test]
fn rejects_overlapping_and_parent_component_paths() {
    let (_dir, mut s) = setup();
    s.pdf = s.documents.join("nested");
    assert!(s.validate().is_err());
    s.pdf = s.documents.join("../Documents");
    assert!(s.validate().is_err());
}
#[test]
fn tags_update_list_references_and_keep_foreign_codes() {
    let (_dir, s) = setup();
    put(&s, "Réseau & accès.docx");
    put(&s, "PROC-00001 Ancien.docx");
    lists::save(
        &s.data.join("Lists.xml"),
        &[list(
            "Installation",
            &["Réseau & accès", "PROC-00001 Ancien"],
        )],
    )
    .unwrap();
    workspace::run(&s, Action::Tags, false, |_| {}).unwrap();
    assert!(s.documents.join("FR-00001 Réseau & accès.docx").exists());
    assert!(s.documents.join("PROC-00001 Ancien.docx").exists());
    let all = lists::load(&s.data.join("Lists.xml")).unwrap();
    assert_eq!(
        all[0].procedures,
        ["FR-00001 Réseau & accès", "PROC-00001 Ancien"]
    );
    let text = fs::read_to_string(s.data.join("Index.xml")).unwrap();
    let xml = roxmltree::Document::parse(&text).unwrap();
    assert_eq!(xml.root_element().attribute("count"), Some("2"));
    assert!(text.contains("Réseau &amp; accès"));
}
#[test]
fn duplicate_codes_abort_before_renaming() {
    let (_dir, s) = setup();
    put(&s, "FR-00001 A.docx");
    put(&s, "fr-00001 B.odt");
    put(&s, "Sans code.docx");
    assert!(workspace::run(&s, Action::Tags, false, |_| {}).is_err());
    assert!(s.documents.join("Sans code.docx").exists());
}
#[test]
fn list_rename_resolution_and_conflict_detection() {
    let (_dir, s) = setup();
    let p = s.data.join("Lists.xml");
    let original = list("Configuration PC", &["FR-00001 Ancien nom"]);
    lists::save(&p, std::slice::from_ref(&original)).unwrap();
    let mut current = lists::load(&p).unwrap();
    assert!(
        lists::synchronize(
            &mut current,
            &["FR-00001 Nouveau nom".into()],
            &HashMap::new()
        )
        .unwrap()
    );
    lists::save(&p, &current).unwrap();
    assert!(lists::commit(&p, Some(&original), None, &[]).is_err());
    assert_eq!(
        lists::load(&p).unwrap()[0].procedures,
        ["FR-00001 Nouveau nom"]
    );
}
#[test]
fn unrelated_list_changes_can_coexist() {
    let (_dir, s) = setup();
    let p = s.data.join("Lists.xml");
    let a = list("A", &["x"]);
    let b = list("B", &["x"]);
    lists::save(&p, &[a.clone(), b.clone()]).unwrap();
    let mut next_a = a.clone();
    next_a.name = "A2".into();
    lists::commit(&p, Some(&a), Some(next_a), &["x".into()]).unwrap();
    lists::commit(&p, Some(&b), None, &[]).unwrap();
    assert_eq!(lists::load(&p).unwrap()[0].name, "A2");
}
#[test]
fn pdf_status_and_orphan_cleanup() {
    let (_dir, s) = setup();
    put(&s, "FR-00001 A.docx");
    let source = s.documents.join("FR-00001 A.docx");
    let pdf = s.pdf.join("FR-00001 A.pdf");
    fs::write(&pdf, b"%PDF-1.7\nexample").unwrap();
    let past = SystemTime::now() - Duration::from_secs(20);
    fs::OpenOptions::new()
        .write(true)
        .open(&pdf)
        .unwrap()
        .set_times(fs::FileTimes::new().set_modified(past))
        .unwrap();
    assert_eq!(workspace::library(&s).unwrap()[0].status(), "pdf-outdated");
    fs::OpenOptions::new()
        .write(true)
        .open(&source)
        .unwrap()
        .set_times(fs::FileTimes::new().set_modified(past - Duration::from_secs(20)))
        .unwrap();
    assert!(workspace::library(&s).unwrap()[0].current);
    fs::write(s.pdf.join("orphan.pdf"), b"%PDF-1.7").unwrap();
    workspace::convert(&s, &mut |_| {}).unwrap();
    assert!(!s.pdf.join("orphan.pdf").exists());
    assert!(pdf.exists());
    // Manual conversion must try even a current PDF. This deliberately invalid
    // DOCX fails conversion; the existing PDF must survive the failed repair.
    assert!(workspace::run(&s, Action::Convert, false, |_| {}).is_err());
    assert_eq!(fs::read(&pdf).unwrap(), b"%PDF-1.7\nexample");
}
#[test]
fn unavailable_source_never_deletes_pdfs() {
    let (_dir, s) = setup();
    fs::write(s.pdf.join("Keep.pdf"), b"%PDF-1.7").unwrap();
    fs::remove_dir(&s.documents).unwrap();
    assert!(workspace::run(&s, Action::Convert, false, |_| {}).is_err());
    assert!(s.pdf.join("Keep.pdf").exists());
}
#[test]
fn archive_preserves_collisions_and_updates_indexes() {
    let (_dir, s) = setup();
    put(&s, "FR-00001 A.docx");
    fs::write(s.archive.join("FR-00001 A.docx"), b"older").unwrap();
    fs::write(s.pdf.join("FR-00001 A.pdf"), b"%PDF-1.7").unwrap();
    lists::save(&s.data.join("Lists.xml"), &[list("A", &["FR-00001 A"])]).unwrap();
    workspace::run(
        &s,
        Action::Archive(vec![s.documents.join("FR-00001 A.docx")]),
        false,
        |_| {},
    )
    .unwrap();
    assert_eq!(
        fs::read(s.archive.join("FR-00001 A.docx")).unwrap(),
        b"older"
    );
    assert!(workspace::library(&s).unwrap().is_empty());
    assert_eq!(workspace::scan(&s.archive, true).unwrap().len(), 2);
    assert!(lists::load(&s.data.join("Lists.xml")).unwrap().is_empty());
}

#[test]
fn automatic_tags_keep_existing_pdf_and_its_date() {
    let (_dir, s) = setup();
    put(&s, "Exemple.docx");
    let pdf = s.pdf.join("Exemple.pdf");
    fs::write(&pdf, b"%PDF-1.7 existing export").unwrap();
    let date = pdf.metadata().unwrap().modified().unwrap();
    lists::save(&s.data.join("Lists.xml"), &[list("A", &["Exemple"])]).unwrap();
    workspace::run(&s, Action::Tags, false, |_| {}).unwrap();
    let renamed = s.pdf.join("FR-00001 Exemple.pdf");
    assert!(
        renamed.exists(),
        "Tag assignment must rename the existing PDF too"
    );
    assert_eq!(fs::read(&renamed).unwrap(), b"%PDF-1.7 existing export");
    assert_eq!(renamed.metadata().unwrap().modified().unwrap(), date);
    assert_eq!(
        lists::load(&s.data.join("Lists.xml")).unwrap()[0].procedures,
        ["FR-00001 Exemple"]
    );
    // No conversion is needed; even an unconvertible source keeps its valid export.
    workspace::run(&s, Action::Full, false, |_| {}).unwrap();
    assert!(renamed.exists());
}

#[test]
fn archive_collision_keeps_document_and_pdf_under_the_same_name() {
    for collide_pdf in [false, true] {
        let (_dir, s) = setup();
        put(&s, "FR-00001 Exemple.docx");
        fs::write(s.pdf.join("FR-00001 Exemple.pdf"), b"current pdf").unwrap();
        let old = s.archive.join(if collide_pdf {
            "FR-00001 Exemple.pdf"
        } else {
            "FR-00001 Exemple.docx"
        });
        fs::write(&old, b"old archive").unwrap();
        workspace::run(
            &s,
            Action::Archive(vec![s.documents.join("FR-00001 Exemple.docx")]),
            false,
            |_| {},
        )
        .unwrap();
        let archived_source = workspace::scan(&s.archive, false)
            .unwrap()
            .into_iter()
            .find(|p| fs::read(p).unwrap() == b"document")
            .unwrap();
        let archived_pdf = archived_source.with_extension("pdf");
        assert_eq!(fs::read(&archived_pdf).unwrap(), b"current pdf");
        assert_eq!(fs::read(&old).unwrap(), b"old archive");
    }
}

#[cfg(windows)]
#[test]
fn failed_archive_restores_document_and_pdf_without_extra_archives() {
    use std::os::windows::fs::OpenOptionsExt;
    let (_dir, s) = setup();
    let source = s.documents.join("FR-00001 Exemple.docx");
    put(&s, "FR-00001 Exemple.docx");
    let pdf = s.pdf.join("FR-00001 Exemple.pdf");
    fs::write(&pdf, b"current pdf").unwrap();
    lists::save(
        &s.data.join("Lists.xml"),
        &[list("A", &["FR-00001 Exemple"])],
    )
    .unwrap();
    let _lock = fs::OpenOptions::new()
        .read(true)
        .share_mode(1)
        .open(&pdf)
        .unwrap();
    assert!(workspace::run(&s, Action::Archive(vec![source.clone()]), false, |_| {}).is_err());
    assert!(
        source.exists(),
        "Failed PDF archival must restore the source document"
    );
    assert_eq!(fs::read(&pdf).unwrap(), b"current pdf");
    assert_eq!(fs::read_dir(&s.archive).unwrap().count(), 0);
    assert_eq!(
        lists::load(&s.data.join("Lists.xml")).unwrap()[0].procedures,
        ["FR-00001 Exemple"]
    );
}

#[test]
fn folder_named_pdf_is_not_a_document_export() {
    let (_dir, s) = setup();
    put(&s, "FR-00001 Exemple.docx");
    fs::create_dir(s.pdf.join("FR-00001 Exemple.pdf")).unwrap();
    let docs = workspace::library(&s).unwrap();
    assert!(docs[0].pdf.is_none());
    assert_eq!(docs[0].status(), "pdf-missing");
}

#[cfg(windows)]
#[test]
fn archive_batch_rolls_back_all_pairs_and_rejects_stale_selection_upfront() {
    use std::os::windows::fs::OpenOptionsExt;
    let (_dir, s) = setup();
    let mut paths = vec![];
    for name in ["FR-00001 A", "FR-00002 B"] {
        let source = s.documents.join(format!("{name}.docx"));
        fs::write(&source, name.as_bytes()).unwrap();
        fs::write(s.pdf.join(format!("{name}.pdf")), name.as_bytes()).unwrap();
        paths.push(source);
    }
    let first_date = paths[0].metadata().unwrap().modified().unwrap();
    let stale = vec![paths[0].clone(), s.documents.join("absent.docx")];
    assert!(workspace::run(&s, Action::Archive(stale), false, |_| {}).is_err());
    assert!(paths[0].exists());
    assert_eq!(fs::read_dir(&s.archive).unwrap().count(), 0);
    let lock = fs::OpenOptions::new()
        .read(true)
        .share_mode(1)
        .open(s.pdf.join("FR-00002 B.pdf"))
        .unwrap();
    assert!(workspace::run(&s, Action::Archive(paths.clone()), false, |_| {}).is_err());
    for source in &paths {
        let name = workspace::stem(source);
        assert_eq!(fs::read(source).unwrap(), name.as_bytes());
        assert_eq!(
            fs::read(s.pdf.join(format!("{name}.pdf"))).unwrap(),
            name.as_bytes()
        );
    }
    assert_eq!(paths[0].metadata().unwrap().modified().unwrap(), first_date);
    assert_eq!(fs::read_dir(&s.archive).unwrap().count(), 0);
    drop(lock);
    workspace::run(&s, Action::Archive(paths), false, |_| {}).unwrap();
    assert_eq!(fs::read_dir(&s.archive).unwrap().count(), 4);
    assert!(workspace::library(&s).unwrap().is_empty());
}

#[cfg(windows)]
#[test]
fn automatic_tags_restore_names_if_pdf_cannot_be_renamed() {
    use std::os::windows::fs::OpenOptionsExt;
    let (_dir, s) = setup();
    put(&s, "Exemple.docx");
    let pdf = s.pdf.join("Exemple.pdf");
    fs::write(&pdf, b"existing pdf").unwrap();
    let _lock = fs::OpenOptions::new()
        .read(true)
        .share_mode(1)
        .open(&pdf)
        .unwrap();
    assert!(workspace::run(&s, Action::Tags, false, |_| {}).is_err());
    assert!(s.documents.join("Exemple.docx").exists());
    assert!(!s.documents.join("FR-00001 Exemple.docx").exists());
    assert_eq!(fs::read(pdf).unwrap(), b"existing pdf");
}
#[test]
fn imports_do_not_overwrite_existing_documents() {
    let (_dir, s) = setup();
    let input = tempfile::tempdir().unwrap();
    let p = input.path().join("A.docx");
    fs::write(&p, "incoming").unwrap();
    put(&s, "A.docx");
    assert!(workspace::run(&s, Action::Import(vec![p]), false, |_| {}).is_err());
    assert_eq!(fs::read(s.documents.join("A.docx")).unwrap(), b"document");
}
#[test]
fn lock_is_exclusive_and_released() {
    let (_dir, s) = setup();
    let held = Lease::acquire(&s.data, Duration::ZERO).unwrap();
    assert!(Lease::acquire(&s.data, Duration::from_millis(100)).is_err());
    drop(held);
    assert!(Lease::acquire(&s.data, Duration::ZERO).is_ok());
}

#[test]
fn stale_settings_cannot_rename_documents_with_an_old_prefix() {
    let (dir, s) = setup();
    put(&s, "New procedure.docx");
    let mut changed = s.clone();
    changed.prefix = "NEW".into();
    changed.save().unwrap();
    assert!(workspace::run(&s, Action::Tags, false, |_| {}).is_err());
    assert!(s.documents.join("New procedure.docx").exists());
    workspace::run(
        &Settings::load(dir.path()).unwrap(),
        Action::Tags,
        false,
        |_| {},
    )
    .unwrap();
    assert!(s.documents.join("NEW-00001 New procedure.docx").exists());
}
#[test]
fn scans_archive_recursively_and_ignores_office_lockfiles() {
    let (_dir, s) = setup();
    put(&s, "~$A.docx");
    put(&s, "A.docx");
    fs::create_dir(s.archive.join("2025")).unwrap();
    fs::write(s.archive.join("2025/Ancien.odt"), b"old").unwrap();
    workspace::run(&s, Action::Index, false, |_| {}).unwrap();
    assert_eq!(workspace::library(&s).unwrap().len(), 1);
    let text = fs::read_to_string(s.data.join("Archive.xml")).unwrap();
    assert!(text.contains("archiveRelativePath"));
    assert!(text.contains("Ancien.odt"));
}
#[test]
fn disconnected_custom_data_does_not_reset_settings() {
    let (dir, s) = setup();
    fs::write(
        dir.path().join("Data/.procedurepilot-data-folder"),
        dir.path().join("missing").to_string_lossy().as_bytes(),
    )
    .unwrap();
    assert!(Settings::load(dir.path()).is_err());
    assert!(s.data.join("Settings.xml").exists());
}
#[test]
fn docx_images_tables_headers_and_numbering() {
    let dir = tempfile::tempdir().unwrap();
    let p = dir.path().join("Réseau.docx");
    let mut z = zip::ZipWriter::new(fs::File::create(&p).unwrap());
    let parts = [
        (
            "word/document.xml",
            r#"<w:document xmlns:w="urn:w" xmlns:r="urn:r" xmlns:a="urn:a"><w:body><w:p><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>Réseau &amp; accès</w:t><w:drawing><a:blip r:embed="pic"/></w:drawing></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>Cellule</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:sectPr><w:headerReference r:id="header" w:type="default"/></w:sectPr></w:body></w:document>"#,
        ),
        (
            "word/_rels/document.xml.rels",
            r#"<Relationships><Relationship Id="pic" Target="media/image.png"/><Relationship Id="header" Target="header1.xml"/></Relationships>"#,
        ),
        (
            "word/header1.xml",
            r#"<w:hdr xmlns:w="urn:w"><w:p><w:r><w:t>En-tête</w:t></w:r></w:p></w:hdr>"#,
        ),
        (
            "word/numbering.xml",
            r#"<w:numbering xmlns:w="urn:w"><w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/></w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>"#,
        ),
        ("word/media/image.png", "test-image"),
    ];
    for (name, text) in parts {
        z.start_file(name, zip::write::SimpleFileOptions::default())
            .unwrap();
        z.write_all(text.as_bytes()).unwrap();
    }
    z.finish().unwrap();
    let html = docx::render(&p).unwrap();
    for text in [
        "Réseau &amp; accès",
        "<table",
        "Cellule",
        "<header>",
        "En-tête",
        "data:image/png;base64,",
        "1.</span>",
    ] {
        assert!(html.contains(text), "Missing {text}");
    }
}
#[test]
fn atomic_xml_replacement_and_log_escaping() {
    let (_dir, s) = setup();
    let p = s.data.join("test.xml");
    storage::atomic_write(&p, b"first").unwrap();
    storage::atomic_write(&p, b"second").unwrap();
    assert_eq!(fs::read(p).unwrap(), b"second");
    storage::log(&s.data, true, "hello\nworld").unwrap();
    let log = fs::read_to_string(s.data.join("Logs.txt")).unwrap();
    assert_eq!(log.lines().count(), 1);
    assert!(log.contains("USER"));
}
