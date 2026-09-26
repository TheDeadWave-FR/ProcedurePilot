use procedure_pilot::{settings::Settings, workspace};
use std::{fs, process::Command};

#[test]
fn concurrent_processes_assign_tags_without_collisions() {
    let dir = tempfile::tempdir().unwrap();
    let s = Settings::initialize(dir.path()).unwrap();
    for i in 0..30 {
        fs::write(s.documents.join(format!("Procedure {i:02}.docx")), b"doc").unwrap();
    }
    let mut children = Vec::new();
    for i in 0..4 {
        let report = dir.path().join(format!("report-{i}.txt"));
        let child = Command::new(env!("CARGO_BIN_EXE_ProcedurePilot"))
            .arg("--root")
            .arg(dir.path())
            .arg("--tags")
            .arg("--report")
            .arg(&report)
            .spawn()
            .unwrap();
        children.push((child, report));
    }
    for (mut child, report) in children {
        assert!(
            child.wait().unwrap().success(),
            "{}",
            fs::read_to_string(report).unwrap_or_default()
        );
    }
    let docs = workspace::library(&s).unwrap();
    assert_eq!(docs.len(), 30);
    for (i, doc) in docs.iter().enumerate() {
        assert_eq!(doc.name, format!("FR-{:05} Procedure {i:02}", i + 1));
    }
    let xml = fs::read_to_string(s.data.join("Index.xml")).unwrap();
    assert_eq!(
        roxmltree::Document::parse(&xml)
            .unwrap()
            .root_element()
            .attribute("count"),
        Some("30")
    );
}
