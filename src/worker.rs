use anyhow::{Result, bail};
use procedure_pilot::{
    lists::{self, ProcedureList},
    settings::{self, Settings},
    storage::{self, Lease},
    workspace::{self, Action, Document},
};
use std::{
    path::PathBuf,
    sync::mpsc::{self, Receiver, Sender},
    thread,
    time::{Duration, Instant},
};

pub enum Request {
    Folders {
        original: Settings,
        replacement: Settings,
        create: [bool; 4],
    },
    Action(Action),
    Settings {
        original: Settings,
        replacement: Settings,
        language: String,
    },
    List {
        original: Option<ProcedureList>,
        replacement: Option<ProcedureList>,
    },
    Stop,
}
pub enum Event {
    Folders(Box<Settings>, bool),
    FoldersResolved(Box<Settings>),
    Snapshot(Box<Settings>, Vec<Document>, Vec<ProcedureList>),
    Log(String),
    Busy(bool),
    Error(String),
    Language(String),
}
pub fn spawn(
    root: PathBuf,
    initial: Settings,
) -> (Sender<Request>, Receiver<Event>, thread::JoinHandle<()>) {
    let (tx, rx) = mpsc::channel();
    let (events, incoming) = mpsc::channel();
    let handle = thread::spawn(move || worker(root, initial, rx, events));
    (tx, incoming, handle)
}
fn report(tx: &Sender<Event>, s: &Settings, user: bool, text: String) {
    match storage::log(&s.data, user, &text) {
        Ok(line) => {
            let _ = tx.send(Event::Log(line));
        }
        Err(e) => {
            let _ = tx.send(Event::Log(format!(
                "{text} — Journal non enregistré / Log not saved: {e}"
            )));
        }
    }
}
fn snapshot(tx: &Sender<Event>, s: &Settings) -> Result<()> {
    s.check_dirs()?;
    let _lease = match Lease::existing(&s.data, Duration::from_millis(100)) {
        Ok(lease) => lease,
        // Keep the last consistent view and retry on the next poll.
        Err(e) if storage::is_busy(&e) => return Ok(()),
        Err(e) => return Err(e),
    };
    let docs = workspace::library(s)?;
    let all = lists::load(&s.data.join("Lists.xml"))?;
    let _ = tx.send(Event::Snapshot(Box::new(s.clone()), docs, all));
    Ok(())
}
fn worker(root: PathBuf, mut s: Settings, rx: Receiver<Request>, tx: Sender<Event>) {
    let configured = Settings::discover(&root).is_ok_and(|(_, configured)| configured);
    let mut blocked = !configured || !s.unavailable().is_empty();
    let mut location_pending = !blocked;
    let mut initial_pending = false;
    if blocked {
        let first = !configured && !Settings::recovery_paths(&root).iter().any(|p| p.exists());
        let _ = tx.send(Event::Folders(Box::new(s.clone()), first));
    } else {
        match s.refresh_location(&root) {
            Ok(()) => location_pending = false,
            Err(e) if storage::is_busy(&e) => {}
            Err(e) => {
                let _ = tx.send(Event::Error(format!("{e:#}")));
            }
        }
        report(
            &tx,
            &s,
            false,
            "Procedure Pilot · Rust — Démarrage / Startup".into(),
        );
        let initial = workspace::run_at(&s, &root, Action::Index, true, |text| {
            report(&tx, &s, false, text)
        });
        if let Err(e) = initial {
            if storage::is_busy(&e) {
                initial_pending = true;
            } else {
                let _ = tx.send(Event::Error(format!("{e:#}")));
            }
        }
        let _ = snapshot(&tx, &s);
    }
    let mut stable = workspace::snapshot(&s).ok();
    let _ = tx.send(Event::Busy(false));
    let mut pending: Option<(String, Instant)> = None;
    let mut last_poll = Instant::now();
    let mut last_error = String::new();
    loop {
        let req = match rx.recv_timeout(Duration::from_millis(150)) {
            Ok(Request::Stop) | Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Ok(r) => Some(r),
            Err(_) => None,
        };
        if let Some(req) = req {
            if blocked && !matches!(&req, Request::Folders { .. }) {
                let _ = tx.send(Event::Busy(false));
                continue;
            }
            let _ = tx.send(Event::Busy(true));
            let result: Result<()> = (|| {
                match req {
                    Request::Folders {
                        original,
                        replacement,
                        create,
                    } => {
                        replacement.install(&root, &original, create)?;
                        s = replacement;
                        blocked = false;
                        let _ = tx.send(Event::FoldersResolved(Box::new(s.clone())));
                        // A recreated source folder is empty; do not delete PDFs or list entries.
                        let _lease = Lease::existing(&s.data, Duration::from_secs(10))?;
                        workspace::indexes_preserving_lists(&s)?;
                        report(
                            &tx,
                            &s,
                            true,
                            "Dossiers configurés / Folders configured".into(),
                        );
                    }
                    Request::Action(action) => {
                        workspace::run_at(&s, &root, action, false, |text| {
                            report(&tx, &s, true, text)
                        })?
                    }
                    Request::Settings {
                        original,
                        replacement,
                        language,
                    } => {
                        replacement.validate()?;
                        s.check_dirs()?;
                        let _old = Lease::existing(&s.data, Duration::from_secs(30))?;
                        if Settings::load(&root)? != original {
                            bail!(
                                "Conflit de paramètres : rechargez la fenêtre / Settings conflict: reopen settings"
                            );
                        }
                        let _new = if s.data != replacement.data {
                            Some(Lease::acquire(&replacement.data, Duration::from_secs(30))?)
                        } else {
                            None
                        };
                        replacement.ensure_dirs()?;
                        if s.data != replacement.data {
                            // Check all destinations before copying any of them.
                            for name in ["Lists.xml", "Logs.txt"] {
                                if replacement.data.join(name).try_exists()? {
                                    bail!(
                                        "Données déjà présentes dans la destination / Destination already contains {name}"
                                    );
                                }
                            }
                            for name in ["Lists.xml", "Logs.txt"] {
                                let old = s.data.join(name);
                                let new = replacement.data.join(name);
                                if old.try_exists()? && new.try_exists()? {
                                    bail!(
                                        "Données déjà présentes dans la destination / Destination already contains {name}"
                                    );
                                }
                                if old.try_exists()? {
                                    storage::atomic_write(&new, &std::fs::read(old)?)?;
                                }
                            }
                        }
                        replacement.save()?;
                        replacement.write_location(&root)?;
                        // Shared language stays unchanged; the chosen language is per Windows user.
                        s = replacement;
                        settings::save_user_language(&language)?;
                        let _ = tx.send(Event::Language(language));
                        workspace::indexes(&s)?;
                        storage::publish(&s.data, "settings")?;
                        report(
                            &tx,
                            &s,
                            true,
                            "Paramètres enregistrés / Settings saved".into(),
                        );
                    }
                    Request::List {
                        original,
                        replacement,
                    } => {
                        s.check_dirs()?;
                        let _lease = Lease::existing(&s.data, Duration::from_secs(10))?;
                        s.check_current(&root)?;
                        let names = workspace::library(&s)?
                            .into_iter()
                            .map(|d| d.name)
                            .collect::<Vec<_>>();
                        lists::commit(
                            &s.data.join("Lists.xml"),
                            original.as_ref(),
                            replacement,
                            &names,
                        )?;
                        storage::publish(&s.data, "lists")?;
                        report(&tx, &s, true, "Listes enregistrées / Lists saved".into());
                    }
                    Request::Stop => {}
                }
                Ok(())
            })();
            if let Err(e) = result {
                let message = format!("{e:#}");
                report(&tx, &s, true, message.clone());
                let _ = tx.send(Event::Error(message));
            }
            if !blocked && let Err(e) = snapshot(&tx, &s) {
                let _ = tx.send(Event::Error(format!("{e:#}")));
            }
            stable = workspace::snapshot(&s).ok();
            pending = None;
            let _ = tx.send(Event::Busy(false));
            last_poll = Instant::now();
        }
        if blocked {
            continue;
        }
        if last_poll.elapsed() < Duration::from_millis(750) {
            continue;
        }
        last_poll = Instant::now();
        if !s.unavailable().is_empty() {
            blocked = true;
            pending = None;
            let _ = tx.send(Event::Folders(Box::new(s.clone()), false));
            continue;
        }
        let result: Result<()> = (|| {
            let loaded = Settings::load(&root)?;
            if loaded != s {
                loaded.validate()?;
                s = loaded;
                location_pending = true;
                stable = None;
                pending = None;
            }
            if !s.unavailable().is_empty() {
                blocked = true;
                let _ = tx.send(Event::Folders(Box::new(s.clone()), false));
                return Ok(());
            }
            if location_pending {
                s.refresh_location(&root)?;
                location_pending = false;
            }
            if initial_pending {
                workspace::run_at(&s, &root, Action::Index, true, |text| {
                    report(&tx, &s, false, text)
                })?;
                initial_pending = false;
            }
            let current = workspace::snapshot(&s)?;
            if stable.as_ref() != Some(&current) {
                match &pending {
                    Some((state, since))
                        if state == &current && since.elapsed() >= Duration::from_millis(1500) =>
                    {
                        let _ = tx.send(Event::Busy(true));
                        let result = workspace::run_at(&s, &root, Action::Full, true, |text| {
                            report(&tx, &s, false, text)
                        });
                        let _ = tx.send(Event::Busy(false));
                        // Lock contention is transient: retain the pending state for the next poll.
                        if result.as_ref().err().is_some_and(storage::is_busy) {
                            return Ok(());
                        }
                        stable = Some(workspace::snapshot(&s)?);
                        pending = None;
                        result?;
                    }
                    Some((state, _)) if state == &current => {}
                    _ => pending = Some((current, Instant::now())),
                }
            } else {
                pending = None;
            }
            snapshot(&tx, &s)?;
            Ok(())
        })();
        if let Err(e) = result {
            if storage::is_busy(&e) {
                continue;
            }
            let text = format!("{e:#}");
            if text != last_error {
                report(&tx, &s, false, text.clone());
                last_error = text;
            }
        } else {
            last_error.clear();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn two_instances_start_during_another_write_then_share_list_updates() {
        let dir = tempfile::tempdir().unwrap();
        let s = Settings::initialize(dir.path()).unwrap();
        std::fs::write(s.documents.join("FR-00001 Procedure.docx"), b"doc").unwrap();
        let held = Lease::existing(&s.data, Duration::ZERO).unwrap();
        let (tx_a, rx_a, worker_a) = spawn(dir.path().to_owned(), s.clone());
        let (tx_b, rx_b, worker_b) = spawn(dir.path().to_owned(), s.clone());
        wait_for(&rx_a, |e| matches!(e, Event::Busy(false)));
        wait_for(&rx_b, |e| matches!(e, Event::Busy(false)));
        drop(held);
        wait_for(&rx_a, |e| matches!(e, Event::Snapshot(_, _, _)));
        wait_for(&rx_b, |e| matches!(e, Event::Snapshot(_, _, _)));
        for (tx, name) in [(&tx_a, "User A"), (&tx_b, "User B")] {
            let mut list = ProcedureList::new();
            list.name = name.into();
            list.procedures = vec!["FR-00001 Procedure".into()];
            tx.send(Request::List {
                original: None,
                replacement: Some(list),
            })
            .unwrap();
        }
        for rx in [&rx_a, &rx_b] {
            wait_for(
                rx,
                |e| matches!(e, Event::Snapshot(_, _, lists) if lists.len() == 2),
            );
        }
        tx_a.send(Request::Stop).unwrap();
        tx_b.send(Request::Stop).unwrap();
        worker_a.join().unwrap();
        worker_b.join().unwrap();
        assert_eq!(lists::load(&s.data.join("Lists.xml")).unwrap().len(), 2);
        assert!(Lease::existing(&s.data, Duration::ZERO).is_ok());
    }

    fn wait_for(rx: &Receiver<Event>, accept: impl Fn(&Event) -> bool) {
        let start = Instant::now();
        while start.elapsed() < Duration::from_secs(8) {
            if let Ok(event) = rx.recv_timeout(Duration::from_millis(100)) {
                if let Event::Error(error) = &event {
                    panic!("Worker error: {error}");
                }
                if accept(&event) {
                    return;
                }
            }
        }
        panic!("Expected worker event not received");
    }
    #[test]
    fn first_launch_waits_without_creating_folders() {
        let dir = tempfile::tempdir().unwrap();
        let s = Settings::discover(dir.path()).unwrap().0;
        let (tx, rx, worker) = spawn(dir.path().to_owned(), s);
        wait_for(&rx, |e| matches!(e, Event::Folders(_, true)));
        wait_for(&rx, |e| matches!(e, Event::Busy(false)));
        assert!(rx.recv_timeout(Duration::from_millis(1000)).is_err());
        tx.send(Request::Stop).unwrap();
        worker.join().unwrap();
        assert_eq!(std::fs::read_dir(dir.path()).unwrap().count(), 0);
    }
    #[test]
    fn live_source_loss_pauses_and_recreation_preserves_pdfs_and_lists() {
        let dir = tempfile::tempdir().unwrap();
        let s = Settings::initialize(dir.path()).unwrap();
        std::fs::write(s.documents.join("FR-00001 Procedure.docx"), b"doc").unwrap();
        std::fs::write(s.pdf.join("FR-00001 Procedure.pdf"), b"pdf").unwrap();
        let mut list = ProcedureList::new();
        list.name = "Keep this list".into();
        list.procedures = vec!["FR-00001 Procedure".into()];
        lists::save(&s.data.join("Lists.xml"), std::slice::from_ref(&list)).unwrap();
        let (tx, rx, worker) = spawn(dir.path().to_owned(), s.clone());
        wait_for(&rx, |e| matches!(e, Event::Busy(false)));
        std::fs::rename(&s.documents, dir.path().join("Documents offline")).unwrap();
        wait_for(&rx, |e| matches!(e, Event::Folders(_, false)));
        assert!(!s.documents.exists());
        tx.send(Request::Folders {
            original: s.clone(),
            replacement: s.clone(),
            create: [true, false, false, false],
        })
        .unwrap();
        wait_for(&rx, |e| matches!(e, Event::FoldersResolved(_)));
        wait_for(&rx, |e| matches!(e, Event::Busy(false)));
        let deadline = Instant::now() + Duration::from_secs(2);
        while Instant::now() < deadline {
            let _ = rx.recv_timeout(Duration::from_millis(100));
        }
        tx.send(Request::Stop).unwrap();
        worker.join().unwrap();
        assert!(s.documents.is_dir());
        assert!(s.pdf.join("FR-00001 Procedure.pdf").exists());
        assert_eq!(lists::load(&s.data.join("Lists.xml")).unwrap()[0], list);
    }
    #[test]
    fn live_data_loss_pauses_and_can_reselect_moved_data() {
        let dir = tempfile::tempdir().unwrap();
        let s = Settings::initialize(dir.path()).unwrap();
        let (tx, rx, worker) = spawn(dir.path().to_owned(), s.clone());
        wait_for(&rx, |e| matches!(e, Event::Busy(false)));
        let moved = dir.path().join("Data moved");
        std::fs::rename(&s.data, &moved).unwrap();
        wait_for(&rx, |e| matches!(e, Event::Folders(_, false)));
        assert!(!s.data.exists());
        let mut replacement = s.clone();
        replacement.data = moved.clone();
        tx.send(Request::Folders {
            original: s,
            replacement: replacement.clone(),
            create: [false; 4],
        })
        .unwrap();
        wait_for(&rx, |e| matches!(e, Event::FoldersResolved(_)));
        wait_for(&rx, |e| matches!(e, Event::Busy(false)));
        tx.send(Request::Stop).unwrap();
        worker.join().unwrap();
        assert_eq!(Settings::load(dir.path()).unwrap(), replacement);
        let locator = dir.path().join("Data");
        assert!(locator.join(".procedurepilot-data-folder").is_file());
        assert!(locator.join(".procedurepilot-recovery.xml").is_file());
        assert_eq!(std::fs::read_dir(&locator).unwrap().count(), 2);
        assert!(moved.join("Settings.xml").is_file());
        assert!(moved.join("Logs.txt").exists());
    }
    #[test]
    fn external_archive_change_is_indexed_and_worker_stops_cleanly() {
        let dir = tempfile::tempdir().unwrap();
        let settings = Settings::initialize(dir.path()).unwrap();
        let (tx, rx, worker) = spawn(dir.path().to_owned(), settings.clone());
        let start = Instant::now();
        while start.elapsed() < Duration::from_secs(5) {
            if matches!(
                rx.recv_timeout(Duration::from_secs(1)),
                Ok(Event::Busy(false))
            ) {
                break;
            }
        }
        std::fs::write(
            settings.archive.join("FR-00001 Archive externe.docx"),
            b"archived",
        )
        .unwrap();
        let start = Instant::now();
        let mut indexed = false;
        while start.elapsed() < Duration::from_secs(8) {
            let _ = rx.recv_timeout(Duration::from_millis(250));
            if std::fs::read_to_string(settings.data.join("Archive.xml"))
                .is_ok_and(|s| s.contains("Archive externe"))
            {
                indexed = true;
                break;
            }
        }
        tx.send(Request::Stop).unwrap();
        worker.join().unwrap();
        assert!(indexed, "The external archive change was not indexed");
        assert!(Lease::acquire(&settings.data, Duration::ZERO).is_ok());
    }
}
