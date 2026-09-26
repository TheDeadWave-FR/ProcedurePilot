use crate::worker::{self, Event, Request};
use eframe::egui::{self, Color32, Frame, Margin, RichText, Stroke, Vec2};
use egui_extras::{Column, TableBuilder};
use procedure_pilot::{
    lists::ProcedureList,
    settings::{self, Settings},
    tr,
    workspace::{Action, Document, EXTENSIONS},
};
use std::{
    collections::{HashSet, VecDeque},
    path::{Path, PathBuf},
    sync::mpsc::{Receiver, Sender},
    time::{Duration, Instant},
};

const NAVY: Color32 = Color32::from_rgb(15, 23, 42);
const INK: Color32 = Color32::from_rgb(30, 41, 59);
const SURFACE: Color32 = Color32::from_rgb(248, 250, 252);
const BORDER: Color32 = Color32::from_rgb(226, 232, 240);
const MUTED: Color32 = Color32::from_rgb(100, 116, 139);
const BLUE: Color32 = Color32::from_rgb(37, 99, 235);
const GREEN: Color32 = Color32::from_rgb(22, 163, 74);
const AMBER: Color32 = Color32::from_rgb(217, 119, 6);

struct SettingsDialog {
    original: Settings,
    paths: [String; 4],
    prefix: String,
    language: String,
}
impl SettingsDialog {
    fn new(s: &Settings, en: bool) -> Self {
        Self {
            original: s.clone(),
            paths: [
                s.documents.display().to_string(),
                s.pdf.display().to_string(),
                s.archive.display().to_string(),
                s.data.display().to_string(),
            ],
            prefix: s.prefix.clone(),
            language: if en { "en".into() } else { "fr".into() },
        }
    }
}
struct Editor {
    original: Option<ProcedureList>,
    draft: ProcedureList,
    search: String,
    selected: Option<usize>,
}
struct RenameDialog {
    source: PathBuf,
    name: String,
    focus: bool,
}
struct FolderDialog {
    original: Settings,
    draft: Settings,
    create: [bool; 4],
    available: [bool; 4],
    first: bool,
    error: Option<String>,
}
impl FolderDialog {
    fn new(s: Settings, first: bool) -> Self {
        let mut dialog = Self {
            original: s.clone(),
            draft: s,
            create: [false; 4],
            available: [true; 4],
            first,
            error: None,
        };
        dialog.refresh();
        if first {
            dialog.create = dialog.available.map(|available| !available);
        }
        dialog
    }
    fn refresh(&mut self) {
        self.available = [true; 4];
        for (i, _) in self.draft.unavailable() {
            self.available[i] = false;
        }
    }
}
enum Confirm {
    Archive(Vec<PathBuf>),
    DeleteList(ProcedureList),
}
pub struct Pilot {
    logo: egui::TextureHandle,
    settings: Settings,
    english: bool,
    documents: Vec<Document>,
    lists: Vec<ProcedureList>,
    search: String,
    selected: HashSet<PathBuf>,
    logs: VecDeque<String>,
    show_log: bool,
    busy: bool,
    error: Option<String>,
    settings_dialog: Option<SettingsDialog>,
    folders_dialog: Option<FolderDialog>,
    show_lists: bool,
    selected_list: Option<String>,
    selected_step: Option<usize>,
    editor: Option<Editor>,
    rename_dialog: Option<RenameDialog>,
    confirm: Option<Confirm>,
    tx: Sender<Request>,
    rx: Receiver<Event>,
    worker: Option<std::thread::JoinHandle<()>>,
    screenshot: Option<PathBuf>,
    started: Instant,
    screenshot_requested: bool,
}
impl Pilot {
    pub fn new(
        cc: &eframe::CreationContext<'_>,
        root: PathBuf,
        s: Settings,
        screenshot: Option<PathBuf>,
    ) -> Self {
        let mut fonts = egui::FontDefinitions::default();
        if let Ok(bytes) = std::fs::read("C:/Windows/Fonts/segoeui.ttf") {
            fonts
                .font_data
                .insert("Segoe UI".into(), egui::FontData::from_owned(bytes).into());
            fonts
                .families
                .entry(egui::FontFamily::Proportional)
                .or_default()
                .insert(0, "Segoe UI".into());
        }
        if let Ok(bytes) = std::fs::read("C:/Windows/Fonts/seguisym.ttf") {
            fonts.font_data.insert(
                "Segoe Symbols".into(),
                egui::FontData::from_owned(bytes).into(),
            );
            fonts
                .families
                .entry(egui::FontFamily::Proportional)
                .or_default()
                .push("Segoe Symbols".into());
        }
        cc.egui_ctx.set_fonts(fonts);
        let mut style = (*cc.egui_ctx.style()).clone();
        style.visuals = egui::Visuals::light();
        style.visuals.panel_fill = SURFACE;
        style.visuals.window_fill = SURFACE;
        style.visuals.override_text_color = Some(INK);
        style.visuals.selection.bg_fill = Color32::from_rgb(219, 234, 254);
        style.visuals.selection.stroke = Stroke::new(1.0_f32, BLUE);
        style.visuals.widgets.inactive.bg_fill = Color32::WHITE;
        style.visuals.widgets.inactive.weak_bg_fill = Color32::WHITE;
        style.visuals.widgets.inactive.bg_stroke = Stroke::new(1.0_f32, BORDER);
        style.visuals.widgets.inactive.corner_radius = egui::CornerRadius::ZERO;
        style.visuals.widgets.hovered.corner_radius = egui::CornerRadius::ZERO;
        style.visuals.widgets.active.corner_radius = egui::CornerRadius::ZERO;
        style.visuals.window_corner_radius = egui::CornerRadius::same(3);
        style.spacing.button_padding = Vec2::new(12.0, 8.0);
        style.spacing.item_spacing = Vec2::new(10.0, 8.0);
        style
            .text_styles
            .insert(egui::TextStyle::Body, egui::FontId::proportional(14.0));
        style
            .text_styles
            .insert(egui::TextStyle::Button, egui::FontId::proportional(13.0));
        cc.egui_ctx.set_style(style);
        let logo_image =
            image::load_from_memory(include_bytes!(concat!(env!("OUT_DIR"), "/app-icon.png")))
                .expect("Embedded application icon")
                .to_rgba8();
        let logo = cc.egui_ctx.load_texture(
            "procedure-pilot-logo",
            egui::ColorImage::from_rgba_unmultiplied(
                [logo_image.width() as usize, logo_image.height() as usize],
                logo_image.as_raw(),
            ),
            egui::TextureOptions::LINEAR,
        );
        let english = settings::user_language(&s.language) == "en";
        let (tx, rx, worker) = worker::spawn(root, s.clone());
        Self {
            logo,
            settings: s,
            english,
            documents: vec![],
            lists: vec![],
            search: String::new(),
            selected: HashSet::new(),
            logs: VecDeque::new(),
            show_log: true,
            busy: true,
            error: None,
            settings_dialog: None,
            folders_dialog: None,
            show_lists: false,
            selected_list: None,
            selected_step: None,
            editor: None,
            rename_dialog: None,
            confirm: None,
            tx,
            rx,
            worker: Some(worker),
            screenshot,
            started: Instant::now(),
            screenshot_requested: false,
        }
    }
    fn send(&mut self, request: Request) {
        if self.busy || self.folders_dialog.is_some() {
            return;
        }
        match self.tx.send(request) {
            Ok(()) => self.busy = true,
            Err(e) => self.error = Some(e.to_string()),
        }
    }
    fn open(&mut self, path: &Path) {
        if !path.exists() {
            self.error = Some(format!(
                "{} : {}",
                tr(self.english, "Fichier introuvable", "File not found"),
                path.display()
            ));
            return;
        }
        #[cfg(windows)]
        let result = std::process::Command::new("explorer.exe").arg(path).spawn();
        #[cfg(not(windows))]
        let result = std::process::Command::new("xdg-open").arg(path).spawn();
        if let Err(e) = result {
            self.error = Some(e.to_string());
        }
    }
    fn open_document(&mut self, d: &Document, pdf: bool) {
        if pdf {
            if let Some(p) = &d.pdf {
                self.open(p)
            } else {
                self.error = Some(
                    tr(
                        self.english,
                        "Aucun PDF disponible pour cette procédure.",
                        "No PDF available for this procedure.",
                    )
                    .into(),
                );
            }
        } else {
            self.open(&d.source)
        }
    }
    fn drain(&mut self) {
        while let Ok(event) = self.rx.try_recv() {
            match event {
                Event::Folders(s, first) => {
                    self.folders_dialog = Some(FolderDialog::new(*s, first));
                    self.settings_dialog = None;
                    self.editor = None;
                    self.show_lists = false;
                    self.confirm = None;
                    self.error = None;
                }
                Event::FoldersResolved(s) => {
                    self.settings = *s;
                    self.folders_dialog = None;
                }
                Event::Snapshot(s, docs, lists) => {
                    self.settings = *s;
                    self.documents = docs;
                    self.lists = lists;
                    self.selected
                        .retain(|p| self.documents.iter().any(|d| &d.source == p));
                    if self
                        .selected_list
                        .as_ref()
                        .is_none_or(|id| !self.lists.iter().any(|l| &l.id == id))
                    {
                        self.selected_list = self.lists.first().map(|l| l.id.clone());
                        self.selected_step = None;
                    }
                }
                Event::Log(line) => {
                    self.logs.push_back(line);
                    while self.logs.len() > 1000 {
                        self.logs.pop_front();
                    }
                }
                Event::Busy(b) => self.busy = b,
                Event::Error(e) => {
                    if let Some(dialog) = &mut self.folders_dialog {
                        dialog.error = Some(e);
                    } else {
                        self.error = Some(e);
                    }
                }
                Event::Language(l) => self.english = l == "en",
            }
        }
    }
    fn header(&mut self, ctx: &egui::Context) {
        let en = self.english;
        // Keep full labels at the minimum window width; all actions remain in the header.
        let single_row = ctx.screen_rect().width() >= 1560.0;
        egui::TopBottomPanel::top("header")
            .exact_height(if single_row { 88.0 } else { 144.0 })
            .frame(
                Frame::new()
                    .fill(NAVY)
                    .inner_margin(Margin::symmetric(28, 18)),
            )
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    ui.add(egui::Image::new(&self.logo).fit_to_exact_size(Vec2::splat(52.0)))
                        .on_hover_text("Procedure Pilot");
                    ui.add_space(10.0);
                    ui.vertical(|ui| {
                        ui.label(
                            RichText::new("PROCEDURE PILOT")
                                .size(22.0)
                                .strong()
                                .color(Color32::WHITE),
                        );
                        ui.label(
                            RichText::new(tr(
                                en,
                                "Centre de gestion documentaire",
                                "Document management center",
                            ))
                            .size(13.0)
                            .color(Color32::from_rgb(148, 163, 184)),
                        );
                    });
                    if single_row {
                        ui.add_space(20.0);
                        let width = (ui.available_width() - 280.0).clamp(900.0, 1120.0);
                        ui.allocate_ui_with_layout(
                            Vec2::new(width, 42.0),
                            egui::Layout::top_down(egui::Align::Min),
                            |ui| self.actions(ui),
                        );
                    }
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        let color = if self.busy || self.folders_dialog.is_some() {
                            Color32::from_rgb(113, 63, 18)
                        } else {
                            Color32::from_rgb(20, 83, 45)
                        };
                        let (badge, _) =
                            ui.allocate_exact_size(Vec2::new(132.0, 30.0), egui::Sense::hover());
                        ui.painter().rect_filled(badge, 0.0, color);
                        ui.painter().text(
                            badge.center(),
                            egui::Align2::CENTER_CENTER,
                            if self.folders_dialog.is_some() {
                                tr(en, "À CONFIGURER", "SETUP REQUIRED")
                            } else if self.busy {
                                tr(en, "EN COURS…", "WORKING…")
                            } else {
                                tr(en, "PRÊT", "READY")
                            },
                            egui::FontId::proportional(12.0),
                            Color32::from_rgb(187, 247, 208),
                        );
                        if ui
                            .add_enabled(
                                !self.busy,
                                egui::Button::new(
                                    RichText::new(tr(en, "PARAMÈTRES", "SETTINGS"))
                                        .color(Color32::WHITE),
                                )
                                .fill(Color32::from_rgb(51, 65, 85))
                                .corner_radius(0)
                                .min_size(Vec2::new(118.0, 32.0)),
                            )
                            .clicked()
                        {
                            self.settings_dialog = Some(SettingsDialog::new(&self.settings, en));
                        }
                    });
                });
                if !single_row {
                    ui.add_space(6.0);
                    self.actions(ui);
                }
            });
        egui::TopBottomPanel::bottom("footer")
            .exact_height(34.0)
            .frame(
                Frame::new()
                    .fill(Color32::WHITE)
                    .inner_margin(Margin::symmetric(28, 7)),
            )
            .show(ctx, |ui| {
                ui.label(
                    RichText::new(format!(
                        "{}  ·  {}",
                        tr(en, "Dossier actif", "Active workspace"),
                        self.settings.root.display()
                    ))
                    .size(12.0)
                    .color(MUTED),
                );
            });
    }
    fn actions(&mut self, ui: &mut egui::Ui) {
        let en = self.english;
        ui.spacing_mut().item_spacing = Vec2::new(10.0, 8.0);
        ui.horizontal(|ui| {
            ui.add_enabled_ui(!self.busy, |ui| {
                ui.spacing_mut().item_spacing.x = 0.0;
                if button(
                    ui,
                    tr(en, "TOUT METTRE À JOUR", "UPDATE EVERYTHING"),
                    BLUE,
                    180.0,
                )
                .clicked()
                {
                    self.send(Request::Action(Action::Full));
                }
                egui::menu::menu_custom_button(
                    ui,
                    egui::Button::new(RichText::new("▼").size(12.0).color(Color32::WHITE))
                        .fill(Color32::from_rgb(29, 78, 216))
                        .corner_radius(0)
                        .stroke(Stroke::NONE)
                        .min_size(Vec2::new(38.0, 42.0)),
                    |ui| {
                        ui.spacing_mut().item_spacing = Vec2::new(10.0, 8.0);
                        for (label, action) in [
                            (tr(en, "Mise à jour des tags", "Update tags"), Action::Tags),
                            (
                                tr(en, "Convertir en PDF", "Convert to PDF"),
                                Action::Convert,
                            ),
                            (
                                tr(en, "Actualiser les index XML", "Refresh XML indexes"),
                                Action::Index,
                            ),
                        ] {
                            if ui.button(label).clicked() {
                                self.send(Request::Action(action));
                                ui.close_menu();
                            }
                        }
                    },
                );
                ui.add_space(10.0);
                if button(
                    ui,
                    tr(en, "Ajouter un document", "Add a document"),
                    Color32::from_rgb(71, 85, 105),
                    155.0,
                )
                .clicked()
                    && let Some(files) = rfd::FileDialog::new()
                        .set_directory(&self.settings.documents)
                        .add_filter(
                            tr(en, "Documents modifiables", "Editable documents"),
                            EXTENSIONS,
                        )
                        .pick_files()
                {
                    self.send(Request::Action(Action::Import(files)));
                }
                ui.add_space(10.0);
                if button(
                    ui,
                    tr(en, "Listes de procédures", "Procedure lists"),
                    Color32::from_rgb(14, 116, 144),
                    165.0,
                )
                .clicked()
                {
                    self.show_lists = true;
                }
            });
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui
                    .add_sized(
                        [140.0, 42.0],
                        egui::Button::new(tr(en, "Ouvrir le dossier", "Open folder"))
                            .fill(Color32::WHITE)
                            .corner_radius(0)
                            .stroke(Stroke::new(1.0_f32, BORDER)),
                    )
                    .clicked()
                {
                    self.open(&self.settings.root.clone());
                }
                let label = if self.show_log {
                    tr(en, "Masquer le journal", "Hide activity log")
                } else {
                    tr(en, "Afficher le journal", "Show activity log")
                };
                if ui
                    .add_sized(
                        [160.0, 42.0],
                        egui::Button::new(label)
                            .fill(Color32::WHITE)
                            .corner_radius(0)
                            .stroke(Stroke::new(1.0_f32, BORDER)),
                    )
                    .clicked()
                {
                    self.show_log = !self.show_log;
                }
            });
        });
    }
    fn main_panel(&mut self, ctx: &egui::Context) {
        let en = self.english;
        egui::CentralPanel::default()
            .frame(
                Frame::new()
                    .fill(SURFACE)
                    .inner_margin(Margin::symmetric(28, 22)),
            )
            .show(ctx, |ui| {
                let counts = [
                    self.documents.len(),
                    self.documents.iter().filter(|d| d.pdf.is_some()).count(),
                    self.documents.iter().filter(|d| d.current).count(),
                ];
                let titles = [
                    tr(en, "DOCUMENTS SOURCES", "SOURCE DOCUMENTS"),
                    tr(en, "PDF DISPONIBLES", "AVAILABLE PDFS"),
                    tr(en, "PRÊTS À CONSULTER", "READY TO VIEW"),
                ];
                ui.spacing_mut().item_spacing = Vec2::new(16.0, 0.0);
                ui.columns(3, |cols| {
                    for i in 0..3 {
                        let accent = [BLUE, GREEN, AMBER][i];
                        let ui = &mut cols[i];
                        let (card, _) = ui.allocate_exact_size(
                            Vec2::new(ui.available_width(), 96.0),
                            egui::Sense::hover(),
                        );
                        ui.painter().rect_filled(card, 0.0, Color32::WHITE);
                        ui.painter().rect_filled(
                            egui::Rect::from_min_size(card.min, Vec2::new(5.0, card.height())),
                            0.0,
                            accent,
                        );
                        ui.painter().text(
                            card.min + Vec2::new(30.0, 16.0),
                            egui::Align2::LEFT_TOP,
                            counts[i].to_string(),
                            egui::FontId::proportional(32.0),
                            NAVY,
                        );
                        ui.painter().text(
                            card.min + Vec2::new(30.0, 56.0),
                            egui::Align2::LEFT_TOP,
                            titles[i],
                            egui::FontId::proportional(11.0),
                            MUTED,
                        );
                    }
                });
                ui.add_space(16.0);
                let log_height = if self.show_log {
                    (ctx.screen_rect().height() * 0.21).clamp(130.0, 210.0)
                } else {
                    0.0
                };
                let log_space = if self.show_log { log_height + 8.0 } else { 0.0 };
                let library_height = (ui.available_height() - log_space).max(150.0);
                Frame::new()
                    .fill(Color32::WHITE)
                    .inner_margin(Margin::symmetric(22, 18))
                    .show(ui, |ui| {
                        ui.spacing_mut().item_spacing = Vec2::new(10.0, 8.0);
                        ui.set_min_height(library_height - 36.0);
                        ui.set_width(ui.available_width());
                        ui.horizontal(|ui| {
                            ui.label(
                                RichText::new(tr(
                                    en,
                                    "BIBLIOTHÈQUE DES PROCÉDURES",
                                    "PROCEDURE LIBRARY",
                                ))
                                .size(14.0)
                                .strong(),
                            );
                            let count = self
                                .documents
                                .iter()
                                .filter(|d| {
                                    d.name.to_lowercase().contains(&self.search.to_lowercase())
                                })
                                .count();
                            ui.label(
                                RichText::new(if self.search.is_empty() {
                                    format!("{count} {}", tr(en, "éléments", "items"))
                                } else {
                                    format!("{count} / {}", self.documents.len())
                                })
                                .size(12.0)
                                .color(MUTED),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    Frame::new()
                                        .fill(Color32::WHITE)
                                        .stroke(Stroke::new(1.0_f32, BORDER))
                                        .inner_margin(Margin::symmetric(8, 5))
                                        .show(ui, |ui| {
                                            ui.add_sized(
                                                [264.0, 18.0],
                                                egui::TextEdit::singleline(&mut self.search)
                                                    .frame(false)
                                                    .hint_text(tr(
                                                        en,
                                                        "Nom ou code de procédure",
                                                        "Procedure name or code",
                                                    )),
                                            );
                                        });
                                    ui.label(
                                        RichText::new(tr(en, "Rechercher", "Search")).color(MUTED),
                                    );
                                },
                            );
                        });
                        ui.add_space(8.0);
                        self.table(ui, (library_height - 112.0).max(38.0));
                    });
                if self.show_log {
                    ui.add_space(8.0);
                    Frame::new()
                        .fill(NAVY)
                        .inner_margin(Margin::symmetric(22, 12))
                        .show(ui, |ui| {
                            ui.spacing_mut().item_spacing = Vec2::new(10.0, 8.0);
                            ui.set_width(ui.available_width());
                            ui.set_min_height(log_height - 24.0);
                            ui.horizontal(|ui| {
                                ui.label(
                                    RichText::new(tr(en, "JOURNAL D’ACTIVITÉ", "ACTIVITY LOG"))
                                        .size(12.0)
                                        .strong()
                                        .color(Color32::from_rgb(203, 213, 225)),
                                );
                                if self.busy {
                                    ui.spinner();
                                }
                            });
                            egui::ScrollArea::both()
                                .id_salt("activity")
                                .stick_to_bottom(true)
                                .max_height(log_height - 51.0)
                                .show(ui, |ui| {
                                    ui.spacing_mut().item_spacing.y = 2.0;
                                    for line in &self.logs {
                                        ui.add(
                                            egui::Label::new(
                                                RichText::new(display_log(line, en))
                                                    .font(egui::FontId::monospace(11.0))
                                                    .color(Color32::from_rgb(203, 213, 225)),
                                            )
                                            .wrap_mode(egui::TextWrapMode::Extend),
                                        );
                                    }
                                });
                        });
                }
            });
    }
    fn table(&mut self, ui: &mut egui::Ui, height: f32) {
        let en = self.english;
        let query = self.search.to_lowercase();
        let docs: Vec<Document> = self
            .documents
            .iter()
            .filter(|d| d.name.to_lowercase().contains(&query))
            .cloned()
            .collect();
        let mut to_open: Option<(Document, bool)> = None;
        let mut archive = false;
        let empty_center =
            ui.cursor().min + Vec2::new(ui.available_width() * 0.5, 28.0 + height * 0.5);
        // Keep column dragging, but reveal the full-height divider only on hover.
        ui.visuals_mut().widgets.noninteractive.bg_stroke = Stroke::NONE;
        TableBuilder::new(ui)
            .striped(false)
            .resizable(true)
            .cell_layout(egui::Layout::left_to_right(egui::Align::Center))
            .sense(egui::Sense::click())
            .max_scroll_height(height)
            .min_scrolled_height(height)
            .column(Column::remainder().at_least(180.0))
            .column(Column::initial(145.0))
            .column(Column::initial(140.0))
            .column(Column::initial(140.0))
            .column(Column::initial(60.0))
            .column(Column::initial(150.0))
            .header(28.0, |mut row| {
                for title in [
                    tr(en, "Nom", "Name"),
                    tr(en, "État", "Status"),
                    tr(en, "Modifié document", "Document modified"),
                    tr(en, "Modifié PDF", "PDF modified"),
                    tr(en, "Taille", "Size"),
                    tr(en, "Document", "Document"),
                ] {
                    row.col(|ui| {
                        ui.label(RichText::new(title).size(13.0).color(INK));
                        let rect = ui.max_rect();
                        ui.painter().vline(
                            rect.right() + 4.0,
                            rect.y_range().shrink(3.0),
                            Stroke::new(1.0_f32, BORDER),
                        );
                    });
                }
            })
            .body(|body| {
                body.rows(32.0, docs.len(), |mut row| {
                    let d = &docs[row.index()];
                    row.set_selected(self.selected.contains(&d.source));
                    row.col(|ui| {
                        library_label(ui, &d.name);
                    });
                    row.col(|ui| {
                        let (text, color) = if d.current {
                            (tr(en, "À jour", "Up to date"), GREEN)
                        } else if d.pdf.is_some() {
                            (tr(en, "PDF à actualiser", "PDF outdated"), AMBER)
                        } else {
                            (tr(en, "PDF manquant", "PDF missing"), MUTED)
                        };
                        library_label(ui, RichText::new(text).color(color).size(13.0));
                    });
                    row.col(|ui| {
                        let date: chrono::DateTime<chrono::Local> = d.modified.into();
                        library_label(
                            ui,
                            RichText::new(date.format("%d/%m/%Y %H:%M").to_string()).size(12.0),
                        );
                    });
                    row.col(|ui| {
                        let text = d.pdf_modified.map_or_else(
                            || "—".to_owned(),
                            |modified| {
                                let date: chrono::DateTime<chrono::Local> = modified.into();
                                date.format("%d/%m/%Y %H:%M").to_string()
                            },
                        );
                        library_label(ui, RichText::new(text).size(12.0));
                    });
                    row.col(|ui| {
                        library_label(ui, RichText::new(format_size(d.size)).size(12.0));
                    });
                    let response = row.response();
                    if response.clicked() {
                        let multiple = response
                            .ctx
                            .input(|i| i.modifiers.ctrl || i.modifiers.command);
                        if !multiple {
                            self.selected.clear();
                        }
                        if !self.selected.insert(d.source.clone()) {
                            self.selected.remove(&d.source);
                        }
                        if !multiple {
                            to_open = Some((d.clone(), true));
                        }
                    }
                    // Only the five data columns above open the PDF. Keep the
                    // document button out of that response so it opens one file.
                    row.col(|ui| {
                        if ui
                            .button(
                                RichText::new(tr(en, "Editer le documents", "Edit document"))
                                    .size(12.0),
                            )
                            .clicked()
                        {
                            to_open = Some((d.clone(), false));
                        }
                    });
                    row.response().context_menu(|ui| {
                        if !self.selected.contains(&d.source) {
                            self.selected.clear();
                            self.selected.insert(d.source.clone());
                        }
                        if ui
                            .add_enabled(
                                d.pdf.is_some(),
                                egui::Button::new(tr(en, "Ouvrir le PDF", "Open PDF")),
                            )
                            .clicked()
                        {
                            to_open = Some((d.clone(), true));
                            ui.close_menu();
                        }
                        if ui
                            .button(tr(en, "Editer le documents", "Edit document"))
                            .clicked()
                        {
                            to_open = Some((d.clone(), false));
                            ui.close_menu();
                        }
                        ui.separator();
                        if ui
                            .add_enabled(
                                !self.busy,
                                egui::Button::new(tr(
                                    en,
                                    "Renommer la procédure…",
                                    "Rename procedure…",
                                )),
                            )
                            .clicked()
                        {
                            self.rename_dialog = Some(RenameDialog {
                                source: d.source.clone(),
                                name: d.name.clone(),
                                focus: true,
                            });
                            ui.close_menu();
                        }
                        if ui
                            .add_enabled(
                                !self.busy,
                                egui::Button::new(tr(
                                    en,
                                    "Archiver la sélection…",
                                    "Archive selection…",
                                )),
                            )
                            .clicked()
                        {
                            archive = true;
                            ui.close_menu();
                        }
                    });
                });
            });
        if docs.is_empty() {
            let (title, hint) = if query.is_empty() {
                (
                    tr(en, "Aucune procédure pour le moment", "No procedures yet"),
                    tr(
                        en,
                        "Ajoutez un document pour démarrer votre bibliothèque.",
                        "Add a document to start your library.",
                    ),
                )
            } else {
                (
                    tr(en, "Aucun résultat", "No results"),
                    tr(
                        en,
                        "Essayez un autre nom ou code de procédure.",
                        "Try another procedure name or code.",
                    ),
                )
            };
            ui.painter().text(
                empty_center - Vec2::new(0.0, 12.0),
                egui::Align2::CENTER_CENTER,
                title,
                egui::FontId::proportional(15.0),
                INK,
            );
            ui.painter().text(
                empty_center + Vec2::new(0.0, 12.0),
                egui::Align2::CENTER_CENTER,
                hint,
                egui::FontId::proportional(12.0),
                MUTED,
            );
        }
        if let Some((d, pdf)) = to_open {
            self.open_document(&d, pdf);
        }
        if archive {
            self.confirm = Some(Confirm::Archive(self.selected.iter().cloned().collect()));
        }
    }
    fn rename_window(&mut self, ctx: &egui::Context) {
        let Some(mut dialog) = self.rename_dialog.take() else {
            return;
        };
        let en = self.english;
        let mut open = true;
        let mut save = false;
        let mut cancel = false;
        egui::Window::new(tr(en, "Renommer la procédure", "Rename procedure"))
            .id(egui::Id::new("rename-procedure"))
            .open(&mut open)
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, Vec2::ZERO)
            .show(ctx, |ui| {
                ui.set_width(460.0);
                ui.label(tr(
                    en,
                    "Nouveau nom (sans extension)",
                    "New name (without extension)",
                ));
                let input = ui
                    .add(egui::TextEdit::singleline(&mut dialog.name).desired_width(f32::INFINITY));
                if dialog.focus {
                    input.request_focus();
                    dialog.focus = false;
                }
                ui.label(tr(
                    en,
                    "Le document et son PDF seront renommés. Les listes seront mises à jour.",
                    "The document and its PDF will be renamed. Lists will be updated.",
                ));
                let validation = procedure_pilot::workspace::validate_procedure_name(&dialog.name);
                if let Err(error) = &validation {
                    ui.colored_label(AMBER, error.to_string());
                }
                let available = !self.busy && self.folders_dialog.is_none() && validation.is_ok();
                ui.horizontal(|ui| {
                    cancel = ui.button(tr(en, "Annuler", "Cancel")).clicked();
                    save = ui
                        .add_enabled(available, egui::Button::new(tr(en, "Renommer", "Rename")))
                        .clicked();
                });
                save |= available
                    && input.lost_focus()
                    && ui.input(|i| i.key_pressed(egui::Key::Enter));
                cancel |= ui.input(|i| i.key_pressed(egui::Key::Escape));
            });
        if save {
            self.send(Request::Action(Action::Rename {
                source: dialog.source,
                name: dialog.name,
            }));
        } else if open && !cancel {
            self.rename_dialog = Some(dialog);
        }
    }
    fn folders_window(&mut self, ctx: &egui::Context) {
        let Some(mut dialog) = self.folders_dialog.take() else {
            return;
        };
        let en = self.english;
        let mut apply = false;
        egui::Modal::new(egui::Id::new("folder-setup"))
            .frame(Frame::new().fill(SURFACE).inner_margin(24).corner_radius(4))
            .show(ctx, |ui| {
                ui.set_width((ctx.screen_rect().width() - 100.0).min(780.0));
                ui.spacing_mut().interact_size.y = 32.0;
                ui.spacing_mut().button_padding = Vec2::new(12.0, 8.0);
                ui.spacing_mut().item_spacing = Vec2::new(10.0, 10.0);
                ui.heading(tr(en, if dialog.first { "Bienvenue dans Procedure Pilot" } else { "Vérifier les dossiers" }, if dialog.first { "Welcome to Procedure Pilot" } else { "Check your folders" }));
                ui.label(tr(en,
                    if dialog.first { "Choisissez où ranger vos documents. Les dossiers seront créés uniquement après votre confirmation." } else { "Un dossier est introuvable ou inaccessible. Les traitements sont suspendus : recréez-le ou sélectionnez son nouvel emplacement." },
                    if dialog.first { "Choose where to store your documents. Folders are created only after you confirm." } else { "A folder is missing or unavailable. Processing is paused: recreate it or select its new location." }));
                ui.add_space(8.0);
                ui.add_enabled_ui(!self.busy, |ui| {
                    if dialog.first {
                        ui.horizontal(|ui| {
                            ui.label(tr(en, "Emplacement", "Location"));
                            ui.label(RichText::new(dialog.draft.root.display().to_string()).color(MUTED));
                        });
                        if ui.button(tr(en, "Choisir l’emplacement des dossiers…", "Choose folder location…")).clicked()
                            && let Some(root) = rfd::FileDialog::new().set_directory(&dialog.draft.root).pick_folder()
                        {
                            match Settings::discover(&root) {
                                Ok((s, _)) => {
                                    dialog.draft = s;
                                    dialog.refresh();
                                    dialog.create = dialog.available.map(|available| !available);
                                    dialog.error = None;
                                }
                                Err(e) => dialog.error = Some(format!("{e:#}")),
                            }
                        }
                    }
                    egui::ScrollArea::vertical().id_salt("folder-choices")
                        .max_height((ctx.screen_rect().height() - 290.0).max(210.0))
                        .show(ui, |ui| {
                            for (i, name) in [tr(en, "Documents sources", "Source documents"), tr(en, "PDF", "PDFs"), tr(en, "Archives", "Archives"), tr(en, "Données de l’application", "Application data")].iter().enumerate() {
                                Frame::new().fill(Color32::WHITE).inner_margin(10).show(ui, |ui| {
                                    ui.set_width(ui.available_width());
                                    ui.horizontal(|ui| {
                                        ui.label(RichText::new(*name).strong());
                                        let (status, color) = if dialog.available[i] { (tr(en, "Disponible", "Available"), GREEN) }
                                            else if dialog.create[i] { (tr(en, "À créer après confirmation", "Will be created on confirmation"), AMBER) }
                                            else { (tr(en, "Introuvable ou inaccessible", "Missing or unavailable"), AMBER) };
                                        ui.label(RichText::new(status).size(12.0).color(color));
                                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                                            if ui.button(tr(en, "Resélectionner…", "Select folder…")).clicked()
                                                && let Some(path) = rfd::FileDialog::new().set_directory(&dialog.draft.root).pick_folder()
                                            {
                                                *match i { 0 => &mut dialog.draft.documents, 1 => &mut dialog.draft.pdf, 2 => &mut dialog.draft.archive, _ => &mut dialog.draft.data } = path;
                                                dialog.create[i] = false;
                                                dialog.refresh();
                                                dialog.error = None;
                                            }
                                            if !dialog.available[i] {
                                                ui.checkbox(&mut dialog.create[i], if dialog.first { tr(en, "Créer ici", "Create here") } else { tr(en, "Recréer ici", "Recreate here") });
                                            }
                                        });
                                    });
                                    ui.label(RichText::new(dialog.draft.folders()[i].display().to_string()).size(12.0).color(MUTED));
                                });
                            }
                        });
                    ui.label(RichText::new(tr(en, "Recréer un dossier ne restaure pas ses fichiers. Sélectionner un dossier existant ne déplace pas son contenu.", "Recreating a folder does not restore its files. Selecting an existing folder does not move its contents.")).size(12.0).color(MUTED));
                    if let Some(error) = &dialog.error { ui.colored_label(Color32::from_rgb(185, 28, 28), error); }
                    ui.add_space(6.0);
                    ui.horizontal(|ui| {
                        if ui.button(tr(en, "Quitter", "Quit")).clicked() { ctx.send_viewport_cmd(egui::ViewportCommand::Close); }
                        if ui.button(tr(en, "Réessayer la détection", "Check again")).clicked() { dialog.refresh(); dialog.error = None; }
                        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                            let valid = (0..4).all(|i| dialog.available[i] || dialog.create[i]);
                            let label = if dialog.create.iter().any(|create| *create) { tr(en, "Créer et continuer", "Create and continue") } else { tr(en, "Continuer", "Continue") };
                            if ui.add_enabled(valid, egui::Button::new(RichText::new(label).color(Color32::WHITE)).fill(BLUE)).clicked() { apply = true; }
                        });
                    });
                });
                if self.busy { ui.horizontal(|ui| { ui.spinner(); ui.label(tr(en, "Configuration en cours…", "Applying folder settings…")); }); }
            });
        if apply {
            dialog.error = None;
            match self.tx.send(Request::Folders {
                original: dialog.original.clone(),
                replacement: dialog.draft.clone(),
                create: dialog.create,
            }) {
                Ok(()) => self.busy = true,
                Err(e) => dialog.error = Some(e.to_string()),
            }
        }
        self.folders_dialog = Some(dialog);
    }
    fn settings_window(&mut self, ctx: &egui::Context) {
        let Some(mut dialog) = self.settings_dialog.take() else {
            return;
        };
        let en = self.english;
        let mut open = true;
        let mut save = false;
        let mut cancel = false;
        egui::Window::new(tr(en,"Paramètres","Settings")).id(egui::Id::new("settings-window")).open(&mut open).collapsible(false).resizable(false).default_width(710.0).show(ctx,|ui|{
            ui.label(RichText::new(tr(en,"ESPACE DE TRAVAIL","WORKSPACE")).strong());
            ui.label(format!("{} : {}",tr(en,"Dossier principal","Root folder"),dialog.original.root.display()));ui.add_space(10.0);
            for (i,label) in [tr(en,"Documents modifiables","Editable documents"),tr(en,"PDF générés","Generated PDFs"),tr(en,"Archives","Archives"),tr(en,"Données de l’application","Application data")].iter().enumerate(){
                ui.label(*label);ui.horizontal(|ui|{ui.add_sized([590.0,28.0],egui::TextEdit::singleline(&mut dialog.paths[i]));if ui.button("…").clicked()&& let Some(path)=rfd::FileDialog::new().set_directory(&dialog.paths[i]).pick_folder(){dialog.paths[i]=path.display().to_string();}});
            }
            ui.add_space(8.0);ui.label(RichText::new("Index.xml · Archive.xml · Settings.xml · Lists.xml · Logs.txt").size(12.0).color(MUTED));
            ui.separator();ui.horizontal(|ui|{ui.label(tr(en,"Préfixe des tags","Tag prefix"));ui.add_sized([120.0,28.0],egui::TextEdit::singleline(&mut dialog.prefix));ui.label(RichText::new(format!("{}-00001",dialog.prefix.trim().to_uppercase())).color(BLUE));});
            ui.horizontal(|ui|{ui.label(tr(en,"Langue de votre session","Your session language"));egui::ComboBox::from_id_salt("language").selected_text(if dialog.language=="en"{"English"}else{"Français"}).show_ui(ui,|ui|{ui.selectable_value(&mut dialog.language,"fr".into(),"Français");ui.selectable_value(&mut dialog.language,"en".into(),"English");});});
            ui.label(RichText::new(tr(en,"Les chemins et le préfixe sont partagés. La langue est propre à votre utilisateur Windows.","Paths and prefix are shared. Language belongs to your Windows user.")).size(12.0).color(MUTED));ui.add_space(8.0);
            ui.horizontal(|ui|{if ui.button(tr(en,"Valeurs par défaut","Restore defaults")).clicked(){let s=Settings::defaults(&dialog.original.root);dialog.paths=[s.documents.display().to_string(),s.pdf.display().to_string(),s.archive.display().to_string(),s.data.display().to_string()];dialog.prefix=s.prefix;}
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center),|ui|{if ui.button(tr(en,"Annuler","Cancel")).clicked(){cancel=true;}
if ui.add_enabled(!self.busy,egui::Button::new(RichText::new(tr(en,"Enregistrer","Save")).color(Color32::WHITE)).fill(BLUE)).clicked(){save=true;}});
            });
        });
        if save {
            let mut s = dialog.original.clone();
            s.documents = PathBuf::from(dialog.paths[0].trim());
            s.pdf = PathBuf::from(dialog.paths[1].trim());
            s.archive = PathBuf::from(dialog.paths[2].trim());
            s.data = PathBuf::from(dialog.paths[3].trim());
            s.prefix = dialog.prefix.trim().to_uppercase();
            match s.validate() {
                Ok(()) => self.send(Request::Settings {
                    original: dialog.original,
                    replacement: s,
                    language: dialog.language,
                }),
                Err(e) => {
                    self.error = Some(e.to_string());
                    self.settings_dialog = Some(dialog);
                }
            }
        } else if open && !cancel {
            self.settings_dialog = Some(dialog);
        }
    }
    fn lists_window(&mut self, ctx: &egui::Context) {
        if !self.show_lists {
            return;
        }
        let en = self.english;
        let mut open = true;
        let mut edit = None;
        let mut create = false;
        let mut delete = None;
        let mut path = None;
        egui::Window::new(tr(en, "Listes de procédures", "Procedure lists"))
            .id(egui::Id::new("lists-window"))
            .open(&mut open)
            .default_size([940.0, 530.0])
            .min_size([800.0, 450.0])
            .show(ctx, |ui| {
                let selected = self
                    .lists
                    .iter()
                    .find(|l| Some(&l.id) == self.selected_list.as_ref())
                    .cloned();
                ui.columns(2, |cols| {
                    Frame::new()
                        .fill(Color32::WHITE)
                        .inner_margin(14)
                        .show(&mut cols[0], |ui| {
                            ui.set_min_size([300.0, 360.0].into());
                            ui.label(RichText::new(tr(en, "MES LISTES", "MY LISTS")).strong());
                            ui.separator();
                            egui::ScrollArea::vertical()
                                .id_salt("saved-lists")
                                .max_height(340.0)
                                .show(ui, |ui| {
                                    for l in &self.lists {
                                        let response = ui.selectable_label(
                                            self.selected_list.as_ref() == Some(&l.id),
                                            &l.name,
                                        );
                                        if response.clicked() {
                                            self.selected_list = Some(l.id.clone());
                                            self.selected_step = None;
                                        }
                                        if response.double_clicked() {
                                            edit = Some(l.clone());
                                        }
                                    }
                                });
                        });
                    Frame::new()
                        .fill(Color32::WHITE)
                        .inner_margin(14)
                        .show(&mut cols[1], |ui| {
                            ui.set_min_size([340.0, 360.0].into());
                            ui.label(
                                RichText::new(
                                    selected.as_ref().map(|l| l.name.as_str()).unwrap_or(tr(
                                        en,
                                        "Sélectionnez une liste",
                                        "Select a list",
                                    )),
                                )
                                .strong(),
                            );
                            ui.separator();
                            egui::ScrollArea::vertical()
                                .id_salt("list-details")
                                .max_height(340.0)
                                .show(ui, |ui| {
                                    if let Some(l) = &selected {
                                        for (i, name) in l.procedures.iter().enumerate() {
                                            let response = ui.selectable_label(
                                                self.selected_step == Some(i),
                                                format!("{}   {name}", i + 1),
                                            );
                                            if response.clicked() {
                                                self.selected_step = Some(i);
                                            }
                                            if response.double_clicked() {
                                                path = self
                                                    .documents
                                                    .iter()
                                                    .find(|d| &d.name == name)
                                                    .map(|d| {
                                                        d.pdf.clone().unwrap_or(d.source.clone())
                                                    });
                                            }
                                        }
                                    }
                                });
                        });
                });
                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    if ui
                        .add_enabled(
                            !self.busy && !self.documents.is_empty(),
                            egui::Button::new(
                                RichText::new(tr(en, "Nouvelle liste", "New list"))
                                    .color(Color32::WHITE),
                            )
                            .fill(BLUE),
                        )
                        .clicked()
                    {
                        create = true;
                    }
                    if ui
                        .add_enabled(
                            !self.busy && selected.is_some(),
                            egui::Button::new(tr(en, "Modifier", "Edit")),
                        )
                        .clicked()
                    {
                        edit = selected.clone();
                    }
                    if ui
                        .add_enabled(
                            !self.busy && selected.is_some(),
                            egui::Button::new(
                                RichText::new(tr(en, "Supprimer", "Delete"))
                                    .color(Color32::from_rgb(185, 28, 28)),
                            ),
                        )
                        .clicked()
                    {
                        delete = selected.clone();
                    }
                    if ui
                        .add_enabled(
                            self.selected_step.is_some(),
                            egui::Button::new(tr(en, "Ouvrir la procédure", "Open procedure")),
                        )
                        .clicked()
                    {
                        path = selected
                            .as_ref()
                            .and_then(|l| l.procedures.get(self.selected_step?))
                            .and_then(|name| self.documents.iter().find(|d| &d.name == name))
                            .map(|d| d.pdf.clone().unwrap_or(d.source.clone()));
                    }
                });
            });
        self.show_lists = open;
        if create {
            self.editor = Some(Editor {
                original: None,
                draft: ProcedureList::new(),
                search: String::new(),
                selected: None,
            });
        }
        if let Some(l) = edit {
            self.editor = Some(Editor {
                original: Some(l.clone()),
                draft: l,
                search: String::new(),
                selected: None,
            });
        }
        if let Some(l) = delete {
            self.confirm = Some(Confirm::DeleteList(l));
        }
        if let Some(p) = path {
            self.open(&p);
        }
    }
    fn editor_window(&mut self, ctx: &egui::Context) {
        let Some(mut editor) = self.editor.take() else {
            return;
        };
        let en = self.english;
        let mut open = true;
        let mut save = false;
        egui::Window::new(tr(
            en,
            "Composer une liste de procédures",
            "Compose a procedure list",
        ))
        .id(egui::Id::new("list-editor"))
        .open(&mut open)
        .default_size([940.0, 530.0])
        .min_size([800.0, 430.0])
        .show(ctx, |ui| {
            ui.horizontal(|ui| {
                ui.label(tr(en, "Nom de la liste", "List name"));
                ui.add_sized(
                    [570.0, 28.0],
                    egui::TextEdit::singleline(&mut editor.draft.name),
                );
            });
            ui.separator();
            ui.columns(2, |cols| {
                cols[0].label(
                    RichText::new(tr(en, "PROCÉDURES DISPONIBLES", "AVAILABLE PROCEDURES"))
                        .strong(),
                );
                cols[0].add(egui::TextEdit::singleline(&mut editor.search).hint_text(tr(
                    en,
                    "Rechercher",
                    "Search",
                )));
                egui::ScrollArea::vertical()
                    .id_salt("available-steps")
                    .max_height(320.0)
                    .show(&mut cols[0], |ui| {
                        ui.set_min_height(280.0);
                        for d in &self.documents {
                            if !d
                                .name
                                .to_lowercase()
                                .contains(&editor.search.to_lowercase())
                            {
                                continue;
                            }
                            let mut included = editor.draft.procedures.contains(&d.name);
                            if ui.checkbox(&mut included, &d.name).changed() {
                                if included {
                                    editor.draft.procedures.push(d.name.clone());
                                } else {
                                    editor.draft.procedures.retain(|n| n != &d.name);
                                    editor.selected = None;
                                }
                            }
                        }
                    });
                cols[1].label(
                    RichText::new(tr(en, "ORDRE DES PROCÉDURES", "PROCEDURE ORDER")).strong(),
                );
                egui::ScrollArea::vertical()
                    .id_salt("ordered-steps")
                    .max_height(320.0)
                    .show(&mut cols[1], |ui| {
                        ui.set_min_height(280.0);
                        for (i, name) in editor.draft.procedures.iter().enumerate() {
                            if ui
                                .selectable_label(
                                    editor.selected == Some(i),
                                    format!("{}   {name}", i + 1),
                                )
                                .clicked()
                            {
                                editor.selected = Some(i);
                            }
                        }
                    });
                cols[1].horizontal(|ui| {
                    if ui
                        .add_enabled(
                            editor.selected.is_some_and(|i| i > 0),
                            egui::Button::new(tr(en, "Monter", "Move up")),
                        )
                        .clicked()
                    {
                        let i = editor.selected.unwrap();
                        editor.draft.procedures.swap(i, i - 1);
                        editor.selected = Some(i - 1);
                    }
                    if ui
                        .add_enabled(
                            editor
                                .selected
                                .is_some_and(|i| i + 1 < editor.draft.procedures.len()),
                            egui::Button::new(tr(en, "Descendre", "Move down")),
                        )
                        .clicked()
                    {
                        let i = editor.selected.unwrap();
                        editor.draft.procedures.swap(i, i + 1);
                        editor.selected = Some(i + 1);
                    }
                    if ui
                        .add_enabled(
                            editor.selected.is_some(),
                            egui::Button::new(tr(en, "Retirer", "Remove")),
                        )
                        .clicked()
                        && let Some(i) = editor.selected.take()
                    {
                        editor.draft.procedures.remove(i);
                    }
                });
            });
            ui.separator();
            let valid = !editor.draft.name.trim().is_empty() && !editor.draft.procedures.is_empty();
            ui.horizontal(|ui| {
                ui.label(format!(
                    "{} {}",
                    editor.draft.procedures.len(),
                    tr(en, "procédure(s)", "procedure(s)")
                ));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .add_enabled(
                            valid && !self.busy,
                            egui::Button::new(
                                RichText::new(tr(en, "Enregistrer", "Save")).color(Color32::WHITE),
                            )
                            .fill(BLUE),
                        )
                        .clicked()
                    {
                        save = true;
                    }
                });
            });
        });
        if save {
            self.send(Request::List {
                original: editor.original,
                replacement: Some(editor.draft),
            });
        } else if open {
            self.editor = Some(editor);
        }
    }
    fn confirmation(&mut self, ctx: &egui::Context) {
        let Some(confirm) = self.confirm.take() else {
            return;
        };
        let en = self.english;
        let mut accept = false;
        let mut cancel = false;
        egui::Window::new(tr(en, "Confirmer", "Confirm"))
            .id(egui::Id::new("confirmation"))
            .collapsible(false)
            .resizable(false)
            .anchor(egui::Align2::CENTER_CENTER, Vec2::ZERO)
            .show(ctx, |ui| {
                ui.set_max_width(480.0);
                ui.label(match &confirm {
                    Confirm::Archive(paths) => format!(
                        "{} {} ?",
                        tr(
                            en,
                            "Archiver les documents sélectionnés :",
                            "Archive selected documents:"
                        ),
                        paths.len()
                    ),
                    Confirm::DeleteList(l) => format!(
                        "{} « {} » ?",
                        tr(en, "Supprimer la liste", "Delete list"),
                        l.name
                    ),
                });
                ui.horizontal(|ui| {
                    if ui.button(tr(en, "Annuler", "Cancel")).clicked() {
                        cancel = true;
                    }
                    if ui
                        .add_enabled(
                            !self.busy,
                            egui::Button::new(tr(en, "Confirmer", "Confirm")),
                        )
                        .clicked()
                    {
                        accept = true;
                    }
                });
            });
        if accept {
            match confirm {
                Confirm::Archive(p) => self.send(Request::Action(Action::Archive(p))),
                Confirm::DeleteList(l) => self.send(Request::List {
                    original: Some(l),
                    replacement: None,
                }),
            }
        } else if !cancel {
            self.confirm = Some(confirm);
        }
    }
}
impl eframe::App for Pilot {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.drain();
        self.header(ctx);
        self.main_panel(ctx);
        self.settings_window(ctx);
        self.lists_window(ctx);
        self.editor_window(ctx);
        self.rename_window(ctx);
        self.confirmation(ctx);
        if let Some(error) = self.error.clone() {
            let mut open = true;
            let mut dismiss = false;
            egui::Window::new(tr(self.english, "Attention", "Attention"))
                .id(egui::Id::new("error"))
                .open(&mut open)
                .collapsible(false)
                .default_width(580.0)
                .show(ctx, |ui| {
                    ui.label(error);
                    if ui.button("OK").clicked() {
                        dismiss = true;
                    }
                });
            if !open || dismiss {
                self.error = None;
            }
        }
        if ctx.input(|i| i.viewport().close_requested()) && self.busy {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            self.error = Some(
                tr(
                    self.english,
                    "Une opération est en cours. Attendez sa fin avant de fermer.",
                    "An operation is running. Wait for it to finish before closing.",
                )
                .into(),
            );
        }
        if !self.busy && self.folders_dialog.is_none() {
            let dropped: Vec<_> = ctx.input(|i| {
                i.raw
                    .dropped_files
                    .iter()
                    .filter_map(|f| f.path.clone())
                    .collect()
            });
            if !dropped.is_empty() {
                self.send(Request::Action(Action::Import(dropped)));
            }
        }
        self.folders_window(ctx);
        if self.screenshot.is_some()
            && !self.screenshot_requested
            && self.started.elapsed() > Duration::from_secs(3)
        {
            ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(Default::default()));
            self.screenshot_requested = true;
        }
        let shots = ctx.input(|i| {
            i.events
                .iter()
                .filter_map(|e| {
                    if let egui::Event::Screenshot { image, .. } = e {
                        Some(image.clone())
                    } else {
                        None
                    }
                })
                .collect::<Vec<_>>()
        });
        for img in shots {
            if let Some(path) = self.screenshot.take() {
                let pixels: Vec<u8> = img.pixels.iter().flat_map(|c| c.to_array()).collect();
                if let Err(e) = image::save_buffer(
                    &path,
                    &pixels,
                    img.width() as u32,
                    img.height() as u32,
                    image::ColorType::Rgba8,
                ) {
                    self.error = Some(e.to_string());
                } else {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                }
            }
        }
        ctx.request_repaint_after(Duration::from_millis(150));
    }
}
impl Drop for Pilot {
    fn drop(&mut self) {
        let _ = self.tx.send(Request::Stop);
        // Finish any write that started just before the close event reached the UI.
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
fn button(ui: &mut egui::Ui, text: &str, color: Color32, width: f32) -> egui::Response {
    ui.add_sized(
        [width, 42.0],
        egui::Button::new(RichText::new(text).color(Color32::WHITE).strong())
            .fill(color)
            .corner_radius(0)
            .stroke(Stroke::NONE),
    )
}
fn library_label(ui: &mut egui::Ui, text: impl Into<egui::WidgetText>) -> egui::Response {
    // Text selection would consume clicks before the containing PDF cell sees them.
    ui.add(egui::Label::new(text).selectable(false))
}

fn format_size(size: u64) -> String {
    if size >= 1024 * 1024 {
        format!("{:.1} Mo", size as f64 / 1048576.0)
    } else {
        format!("{} Ko", size.div_ceil(1024))
    }
}

fn display_log(line: &str, english: bool) -> String {
    if let Some((timestamp, message)) = line.split_once(' ')
        && let Ok(time) = chrono::DateTime::parse_from_rfc3339(timestamp)
    {
        let message = if !english {
            message.replacen("[SYSTEM]", "[SYSTÈME]", 1)
        } else {
            message.to_owned()
        };
        return format!(
            "{}  {message}",
            time.with_timezone(&chrono::Local).format("%H:%M:%S")
        );
    }
    line.to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn name_text_and_cell_open_pdf_but_edit_button_does_not() {
        fn frame(ctx: &egui::Context, events: Vec<egui::Event>) -> (bool, bool, [egui::Pos2; 3]) {
            let mut pdf_clicked = false;
            let mut edit_clicked = false;
            let mut targets = [egui::Pos2::ZERO; 3];
            let _ = ctx.run(
                egui::RawInput {
                    screen_rect: Some(egui::Rect::from_min_size(
                        egui::Pos2::ZERO,
                        Vec2::new(600.0, 200.0),
                    )),
                    events,
                    ..Default::default()
                },
                |ctx| {
                    egui::CentralPanel::default().show(ctx, |ui| {
                        // Reproduce the text-selection setting that used to steal clicks.
                        ui.style_mut().interaction.selectable_labels = true;
                        TableBuilder::new(ui)
                            .sense(egui::Sense::click())
                            .column(Column::exact(300.0))
                            .column(Column::exact(160.0))
                            .body(|body| {
                                body.rows(32.0, 1, |mut row| {
                                    let (_, cell) = row.col(|ui| {
                                        targets[0] =
                                            library_label(ui, "FR-00001 Exemple").rect.center();
                                    });
                                    targets[1] = cell.rect.right_center() - Vec2::new(10.0, 0.0);
                                    pdf_clicked = row.response().clicked();
                                    row.col(|ui| {
                                        let edit = ui.button("Editer le documents");
                                        targets[2] = edit.rect.center();
                                        edit_clicked = edit.clicked();
                                    });
                                });
                            });
                    });
                },
            );
            (pdf_clicked, edit_clicked, targets)
        }

        for target in 0..3 {
            let ctx = egui::Context::default();
            frame(&ctx, vec![]);
            let (_, _, targets) = frame(&ctx, vec![]);
            let pos = targets[target];
            let event = |pressed| egui::Event::PointerButton {
                pos,
                button: egui::PointerButton::Primary,
                pressed,
                modifiers: egui::Modifiers::default(),
            };
            frame(&ctx, vec![egui::Event::PointerMoved(pos), event(true)]);
            let (pdf_clicked, edit_clicked, _) = frame(&ctx, vec![event(false)]);
            assert_eq!(
                (pdf_clicked, edit_clicked),
                (target != 2, target == 2),
                "target {target}"
            );
        }
    }
}
