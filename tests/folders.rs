use procedure_pilot::{
    settings::Settings,
    storage,
    workspace::{self, Action},
};
use std::fs;

#[test]
fn discovery_does_not_create_anything_on_first_launch() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("New workspace");
    let (s, configured) = Settings::discover(&root).unwrap();
    assert!(!configured);
    assert_eq!(s.unavailable().len(), 4);
    assert!(!root.exists());
}

#[test]
fn first_setup_can_choose_another_root_and_remembers_it() {
    let temp = tempfile::tempdir().unwrap();
    let launcher = temp.path().join("Launcher");
    fs::create_dir(&launcher).unwrap();
    let original = Settings::discover(&launcher).unwrap().0;
    let chosen = Settings::defaults(&temp.path().join("Chosen workspace"));
    chosen.install(&launcher, &original, [true; 4]).unwrap();
    assert_eq!(Settings::load(&launcher).unwrap(), chosen);
    assert!(chosen.unavailable().is_empty());
    assert!(!launcher.join("Documents").exists());
}

#[test]
fn missing_folders_wait_for_explicit_creation() {
    let temp = tempfile::tempdir().unwrap();
    let s = Settings::initialize(temp.path()).unwrap();
    fs::remove_dir(&s.documents).unwrap();
    let (found, configured) = Settings::discover(temp.path()).unwrap();
    assert!(configured);
    assert_eq!(found, s);
    assert_eq!(found.unavailable()[0].0, 0);
    assert!(s.install(temp.path(), &s, [false; 4]).is_err());
    assert!(!s.documents.exists());
    fs::write(s.pdf.join("keep.pdf"), b"pdf").unwrap();
    s.install(temp.path(), &s, [true, false, false, false])
        .unwrap();
    assert!(s.documents.is_dir());
    assert_eq!(fs::read(s.pdf.join("keep.pdf")).unwrap(), b"pdf");
}

#[test]
fn reselecting_an_existing_folder_preserves_its_contents() {
    let temp = tempfile::tempdir().unwrap();
    let s = Settings::initialize(temp.path()).unwrap();
    let moved = temp.path().join("Moved documents");
    fs::write(s.documents.join("Procedure.docx"), b"document").unwrap();
    fs::rename(&s.documents, &moved).unwrap();
    let mut replacement = s.clone();
    replacement.documents = moved.clone();
    replacement.install(temp.path(), &s, [false; 4]).unwrap();
    assert_eq!(Settings::load(temp.path()).unwrap(), replacement);
    assert!(!s.documents.exists());
    assert_eq!(fs::read(moved.join("Procedure.docx")).unwrap(), b"document");
}

#[test]
fn missing_data_is_recoverable_without_resetting_custom_paths() {
    let temp = tempfile::tempdir().unwrap();
    let original = Settings::discover(temp.path()).unwrap().0;
    let mut s = original.clone();
    s.documents = temp.path().join("Custom sources");
    s.data = temp.path().join("Custom data");
    s.prefix = "SUPPORT".into();
    s.install(temp.path(), &original, [true; 4]).unwrap();
    fs::rename(&s.data, temp.path().join("Data offline")).unwrap();
    let (found, configured) = Settings::discover(temp.path()).unwrap();
    assert!(!configured);
    assert_eq!(found, s);
    assert!(!s.data.exists());
    assert!(storage::log(&s.data, false, "No implicit creation").is_err());
    assert!(workspace::run(&s, Action::Index, false, |_| {}).is_err());
    assert!(!s.data.exists());
    s.install(temp.path(), &found, [false, false, false, true])
        .unwrap();
    assert_eq!(Settings::load(temp.path()).unwrap(), s);
}

#[test]
fn all_choices_are_checked_before_creating_any_folder() {
    let temp = tempfile::tempdir().unwrap();
    let s = Settings::defaults(temp.path());
    assert!(
        s.install(temp.path(), &s, [true, true, true, false])
            .is_err()
    );
    assert!(!s.documents.exists());
    fs::write(&s.data, b"Not a directory").unwrap();
    assert!(s.install(temp.path(), &s, [true; 4]).is_err());
    assert!(!s.documents.exists());
}

#[test]
fn setup_preserves_legacy_folders_without_renaming_them() {
    let temp = tempfile::tempdir().unwrap();
    fs::create_dir(temp.path().join("Procedures_Modifiables")).unwrap();
    let (s, configured) = Settings::discover(temp.path()).unwrap();
    assert!(!configured);
    assert_eq!(s.documents, temp.path().join("Procedures_Modifiables"));
    s.install(temp.path(), &s, [false, true, true, true])
        .unwrap();
    assert!(s.documents.exists());
    assert!(!temp.path().join("Documents").exists());
}

#[test]
fn stale_folder_dialog_cannot_overwrite_newer_settings() {
    let temp = tempfile::tempdir().unwrap();
    let s = Settings::initialize(temp.path()).unwrap();
    let mut other = s.clone();
    other.prefix = "CHANGED".into();
    other.save().unwrap();
    assert!(s.install(temp.path(), &s, [false; 4]).is_err());
    assert_eq!(Settings::load(temp.path()).unwrap(), other);
}

#[test]
fn legacy_data_pointer_is_still_resolved() {
    let temp = tempfile::tempdir().unwrap();
    let mut s = Settings::defaults(temp.path());
    s.data = temp.path().join("Shared metadata");
    s.ensure_dirs().unwrap();
    s.save().unwrap();
    fs::create_dir(temp.path().join("Data")).unwrap();
    fs::write(
        temp.path().join("Data/ProcedurePilot.data-folder"),
        s.data.to_string_lossy().as_bytes(),
    )
    .unwrap();
    assert_eq!(Settings::discover(temp.path()).unwrap(), (s, true));
}

#[test]
fn damaged_primary_settings_are_not_silently_replaced_by_recovery() {
    let temp = tempfile::tempdir().unwrap();
    let s = Settings::initialize(temp.path()).unwrap();
    fs::write(s.data.join("Settings.xml"), "<damaged>").unwrap();
    assert!(Settings::discover(temp.path()).is_err());
    assert_eq!(
        fs::read_to_string(s.data.join("Settings.xml")).unwrap(),
        "<damaged>"
    );
}

#[test]
fn metadata_lives_in_data_and_recovers_missing_settings() {
    let temp = tempfile::tempdir().unwrap();
    let s = Settings::initialize(temp.path()).unwrap();
    for name in [
        ".procedurepilot-data-folder",
        ".procedurepilot-recovery.xml",
    ] {
        assert!(s.data.join(name).is_file());
        assert!(!temp.path().join(name).exists());
    }
    fs::remove_file(s.data.join("Settings.xml")).unwrap();
    assert_eq!(Settings::discover(temp.path()).unwrap(), (s, false));
}

#[test]
fn legacy_root_metadata_migrates_without_losing_custom_settings() {
    let temp = tempfile::tempdir().unwrap();
    let mut s = Settings::initialize(temp.path()).unwrap();
    s.data = temp.path().join("Custom data");
    s.prefix = "SUPPORT".into();
    s.language = "en".into();
    s.ensure_dirs().unwrap();
    s.save().unwrap();
    s.write_location(temp.path()).unwrap();
    for name in [
        ".procedurepilot-data-folder",
        ".procedurepilot-recovery.xml",
    ] {
        fs::rename(temp.path().join("Data").join(name), temp.path().join(name)).unwrap();
    }
    assert_eq!(Settings::load(temp.path()).unwrap(), s);
    fs::remove_file(s.data.join("Settings.xml")).unwrap();
    assert_eq!(Settings::discover(temp.path()).unwrap(), (s.clone(), false));
    s.save().unwrap();
    s.write_location(temp.path()).unwrap();
    s.write_location(temp.path()).unwrap();
    for name in [
        ".procedurepilot-data-folder",
        ".procedurepilot-recovery.xml",
    ] {
        assert!(temp.path().join("Data").join(name).is_file());
        assert!(!temp.path().join(name).exists());
    }
    assert_eq!(Settings::load(temp.path()).unwrap(), s);
}

#[test]
fn stale_instance_cannot_rewrite_launcher_metadata() {
    let temp = tempfile::tempdir().unwrap();
    let original = Settings::initialize(temp.path()).unwrap();
    let mut current = original.clone();
    current.data = temp.path().join("Shared data");
    current.prefix = "NEW".into();
    current.ensure_dirs().unwrap();
    current.save().unwrap();
    current.write_location(temp.path()).unwrap();
    assert!(original.refresh_location(temp.path()).is_err());
    assert!(workspace::run_at(&original, temp.path(), Action::Index, false, |_| {}).is_err());
    workspace::run_at(&current, temp.path(), Action::Index, false, |_| {}).unwrap();
    assert_eq!(Settings::load(temp.path()).unwrap(), current);
    fs::remove_file(current.data.join("Settings.xml")).unwrap();
    assert_eq!(Settings::discover(temp.path()).unwrap(), (current, false));
}
