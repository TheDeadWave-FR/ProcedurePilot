//! Offline OOXML renderer. ZIP parts stay in memory; relationships never fetch remote content.
use crate::storage::xml;
use anyhow::{Context, Result, bail};
use base64::Engine;
use roxmltree::{Document, Node};
use std::{
    collections::{HashMap, HashSet},
    fs::File,
    io::Read,
    path::Path,
};

type Parts = HashMap<String, Vec<u8>>;
fn child<'a, 'd>(n: Node<'a, 'd>, name: &str) -> Option<Node<'a, 'd>> {
    n.children()
        .find(|n| n.is_element() && n.tag_name().name() == name)
}
fn attr<'a>(n: Node<'a, '_>, name: &str) -> &'a str {
    n.attributes()
        .find(|a| a.name() == name)
        .map(|a| a.value())
        .unwrap_or("")
}
fn val<'a>(n: Node<'a, '_>, name: &str) -> &'a str {
    child(n, name).map(|c| attr(c, "val")).unwrap_or("")
}
fn num(s: &str, default: f64) -> f64 {
    s.parse::<f64>()
        .ok()
        .filter(|n| n.is_finite())
        .unwrap_or(default)
}
fn on(n: Node<'_, '_>, name: &str) -> bool {
    child(n, name).is_some_and(|c| !matches!(attr(c, "val"), "0" | "false" | "off"))
}
fn hex(s: &str) -> bool {
    s.len() == 6 && s.bytes().all(|b| b.is_ascii_hexdigit())
}
fn font(s: &str) -> String {
    s.chars()
        .filter(|c| c.is_alphanumeric() || matches!(c, ' ' | '-' | '_'))
        .collect()
}
fn css_attr(css: &str) -> String {
    format!(" style=\"{}\"", xml(css))
}
fn part_target(part: &str, target: &str) -> String {
    let joined = if target.starts_with('/') {
        target.trim_start_matches('/').into()
    } else {
        format!(
            "{}/{}",
            part.rsplit_once('/').map(|p| p.0).unwrap_or(""),
            target
        )
    };
    let mut segs = vec![];
    for s in joined.split('/') {
        match s {
            ".." => {
                segs.pop();
            }
            "" | "." => {}
            _ => segs.push(s),
        }
    }
    segs.join("/")
}
fn text_part<'a>(parts: &'a Parts, key: &str) -> Option<&'a str> {
    std::str::from_utf8(parts.get(key)?).ok()
}

pub fn render(path: &Path) -> Result<String> {
    let mut zip = zip::ZipArchive::new(File::open(path)?)?;
    let mut parts = Parts::new();
    let mut total = 0u64;
    for i in 0..zip.len() {
        let mut f = zip.by_index(i)?;
        if f.is_dir() {
            continue;
        }
        total = total.checked_add(f.size()).context("ZIP size overflow")?;
        if total > 256 * 1024 * 1024 || f.size() > 64 * 1024 * 1024 {
            bail!("DOCX trop volumineux / DOCX exceeds memory limit");
        }
        let mut bytes = vec![];
        f.read_to_end(&mut bytes)?;
        parts.insert(f.name().replace('\\', "/"), bytes);
    }
    render_parts(
        &parts,
        &path.file_stem().unwrap_or_default().to_string_lossy(),
    )
}
fn render_parts(parts: &Parts, title: &str) -> Result<String> {
    let text = text_part(parts, "word/document.xml").context("word/document.xml absent")?;
    let doc = Document::parse(text)?;
    let body = doc
        .descendants()
        .find(|n| n.is_element() && n.tag_name().name() == "body")
        .context("DOCX sans corps / Missing document body")?;
    let mut r = Renderer {
        parts,
        styles: text_part(parts, "word/styles.xml")
            .map(Document::parse)
            .transpose()?,
        numbering: text_part(parts, "word/numbering.xml")
            .map(Document::parse)
            .transpose()?,
        counters: HashMap::new(),
    };
    let section = body
        .descendants()
        .rfind(|n| n.is_element() && n.tag_name().name() == "sectPr");
    let size = section.and_then(|s| child(s, "pgSz"));
    let margins = section.and_then(|s| child(s, "pgMar"));
    let mm = |n: Option<Node<'_, '_>>, key: &str, fallback: f64| {
        n.map(|n| num(attr(n, key), fallback * 1440.0 / 25.4) * 25.4 / 1440.0)
            .unwrap_or(fallback)
    };
    let w = mm(size, "w", 210.0);
    let h = mm(size, "h", 297.0);
    let top = mm(margins, "top", 20.0);
    let right = mm(margins, "right", 20.0);
    let bottom = mm(margins, "bottom", 20.0);
    let left = mm(margins, "left", 20.0);
    let default_css = r
        .styles
        .as_ref()
        .and_then(|d| {
            d.descendants()
                .find(|n| n.is_element() && n.tag_name().name() == "rPrDefault")
        })
        .and_then(|n| child(n, "rPr"))
        .map(run_css)
        .unwrap_or_default();
    let mut html = format!(
        "<!doctype html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'\"><title>{}</title><style>@page{{size:{w}mm {h}mm;margin:{top}mm {right}mm {bottom}mm {left}mm}}*{{box-sizing:border-box}}html,body{{margin:0;padding:0}}body{{font-family:Calibri,Arial,sans-serif;font-size:11pt;line-height:1.15;color:black;{default_css}}}p{{margin:0 0 10pt;min-height:1em;white-space:pre-wrap}}table{{border-collapse:collapse;width:100%;margin:0 0 8pt}}td,th{{vertical-align:top;padding:2pt 4pt}}img{{max-width:100%;height:auto}}a{{color:inherit}}.page-break{{display:block;break-after:page;height:0}}.tab{{display:inline-block;width:2.5em}}.marker{{display:inline-block;min-width:2em}}.page-number::after{{content:counter(page)}}header,footer{{position:static}}table.doc-layout{{margin:0;border:0}}table.doc-layout>tbody>tr>td{{padding:0}}table.doc-layout>thead>tr>td{{padding:0 0 {}mm}}table.doc-layout>tfoot>tr>td{{padding:{}mm 0 0}}thead{{display:table-header-group}}tfoot{{display:table-footer-group}}</style></head><body>",
        xml(title),
        (top - mm(margins, "header", 10.0)).max(0.0),
        (bottom - mm(margins, "footer", 10.0)).max(0.0)
    );
    // Repeat rich header/footer blocks using print table groups. Negative fixed positions
    // can be moved to the preceding page by Chromium's pagination engine.
    let mut header_html = String::new();
    let mut footer_html = String::new();
    if let Some(s) = section {
        for (reference, tag) in [("headerReference", "header"), ("footerReference", "footer")] {
            let reference = s
                .children()
                .filter(|n| n.is_element() && n.tag_name().name() == reference)
                .find(|n| attr(*n, "type") == "default")
                .or_else(|| child(s, reference));
            if let Some(n) = reference
                && let Some((target, false)) = r.relationship("word/document.xml", attr(n, "id"))
            {
                let target = part_target("word/document.xml", &target);
                if let Some(text) = text_part(parts, &target) {
                    let d = Document::parse(text)?;
                    let rendered =
                        format!("<{tag}>{}</{tag}>", r.blocks(d.root_element(), &target));
                    if tag == "header" {
                        header_html = rendered;
                    } else {
                        footer_html = rendered;
                    }
                }
            }
        }
    }
    html.push_str(&format!("<table class=\"doc-layout\"><thead><tr><td>{header_html}</td></tr></thead><tfoot><tr><td>{footer_html}</td></tr></tfoot><tbody><tr><td><main>"));
    html.push_str(&r.blocks(body, "word/document.xml"));
    html.push_str("</main></td></tr></tbody></table></body></html>");
    Ok(html)
}
struct Renderer<'a> {
    parts: &'a Parts,
    styles: Option<Document<'a>>,
    numbering: Option<Document<'a>>,
    counters: HashMap<String, [u32; 9]>,
}
impl Renderer<'_> {
    fn relationship(&self, part: &str, id: &str) -> Option<(String, bool)> {
        let (dir, file) = part.rsplit_once('/')?;
        let d =
            Document::parse(text_part(self.parts, &format!("{dir}/_rels/{file}.rels"))?).ok()?;
        let n = d
            .descendants()
            .find(|n| n.is_element() && attr(*n, "Id") == id)?;
        Some((
            attr(n, "Target").into(),
            attr(n, "TargetMode") == "External",
        ))
    }
    fn style(&self, id: &str, paragraph: bool, seen: &mut HashSet<String>) -> String {
        if id.is_empty() || !seen.insert(id.to_owned()) {
            return String::new();
        }
        let Some(n) = self.styles.as_ref().and_then(|d| {
            d.descendants().find(|n| {
                n.is_element() && n.tag_name().name() == "style" && attr(*n, "styleId") == id
            })
        }) else {
            return String::new();
        };
        let mut css = self.style(val(n, "basedOn"), paragraph, seen);
        if paragraph && let Some(p) = child(n, "pPr") {
            css.push_str(&paragraph_css(p));
        }
        if let Some(p) = child(n, "rPr") {
            css.push_str(&run_css(p));
        }
        css
    }
    fn blocks(&mut self, node: Node<'_, '_>, part: &str) -> String {
        let mut out = String::new();
        for n in node.children().filter(Node::is_element) {
            match n.tag_name().name() {
                "p" => {
                    let prop = child(n, "pPr");
                    let id = prop.map(|p| val(p, "pStyle")).unwrap_or("");
                    let mut css = self.style(id, true, &mut HashSet::new());
                    if let Some(p) = prop {
                        css.push_str(&paragraph_css(p));
                    }
                    let style_name = self
                        .styles
                        .as_ref()
                        .and_then(|d| {
                            d.descendants()
                                .find(|n| n.is_element() && attr(*n, "styleId") == id)
                        })
                        .map(|n| val(n, "name"))
                        .unwrap_or(id)
                        .to_lowercase()
                        .replace(' ', "");
                    let tag = match style_name.as_str() {
                        "title" | "titre" | "heading1" | "titre1" => "h1",
                        "heading2" | "titre2" => "h2",
                        "heading3" | "titre3" => "h3",
                        "heading4" | "titre4" => "h4",
                        "heading5" | "titre5" => "h5",
                        "heading6" | "titre6" => "h6",
                        _ => "p",
                    };
                    let marker = self.paragraph_marker(prop, id);
                    let mut content = self.inlines(n, part);
                    if content.is_empty() {
                        content.push_str("&nbsp;");
                    }
                    out.push_str(&format!(
                        "<{tag}{}>{marker}{content}</{tag}>",
                        css_attr(&css)
                    ));
                }
                "tbl" => out.push_str(&self.table(n, part)),
                "sdt" => {
                    if let Some(c) = child(n, "sdtContent") {
                        out.push_str(&self.blocks(c, part));
                    }
                }
                _ => {}
            }
        }
        out
    }
    fn inlines(&mut self, n: Node<'_, '_>, part: &str) -> String {
        let mut out = String::new();
        for c in n.children().filter(Node::is_element) {
            match c.tag_name().name() {
                "r" => {
                    let p = child(c, "rPr");
                    let id = p.map(|p| val(p, "rStyle")).unwrap_or("");
                    let mut css = self.style(id, false, &mut HashSet::new());
                    if let Some(p) = p {
                        css.push_str(&run_css(p));
                    }
                    out.push_str(&format!(
                        "<span{}>{}</span>",
                        css_attr(&css),
                        self.inlines(c, part)
                    ));
                }
                "t" => out.push_str(&xml(c.text().unwrap_or(""))),
                "tab" => out.push_str("<span class=\"tab\">&nbsp;</span>"),
                "br" if attr(c, "type") == "page" => {
                    out.push_str("<span class=\"page-break\"></span>")
                }
                "br" | "cr" => out.push_str("<br>"),
                "noBreakHyphen" => out.push('‑'),
                "softHyphen" => out.push_str("&shy;"),
                "drawing" | "pict" => out.push_str(&self.image(c, part)),
                "hyperlink" => {
                    let contents = self.inlines(c, part);
                    let href = self.relationship(part, attr(c, "id")).filter(|(u, _)| {
                        u.starts_with("https://")
                            || u.starts_with("http://")
                            || u.starts_with("mailto:")
                    });
                    if let Some((url, _)) = href {
                        out.push_str(&format!("<a href=\"{}\">{contents}</a>", xml(url)));
                    } else {
                        out.push_str(&contents);
                    }
                }
                "fldSimple" if attr(c, "instr").trim() == "PAGE" => {
                    out.push_str("<span class=\"page-number\"></span>")
                }
                "smartTag" | "ins" | "sdt" | "sdtContent" | "fldSimple" => {
                    out.push_str(&self.inlines(c, part))
                }
                _ => {}
            }
        }
        out
    }
    fn image(&self, n: Node<'_, '_>, part: &str) -> String {
        let Some(blip) = n
            .descendants()
            .find(|n| n.is_element() && matches!(n.tag_name().name(), "blip" | "imagedata"))
        else {
            return String::new();
        };
        let id = if attr(blip, "embed").is_empty() {
            attr(blip, "id")
        } else {
            attr(blip, "embed")
        };
        let Some((target, false)) = self.relationship(part, id) else {
            return String::new();
        };
        let target = part_target(part, &target);
        let Some(data) = self.parts.get(&target) else {
            return String::new();
        };
        let mime = match target
            .rsplit('.')
            .next()
            .unwrap_or("")
            .to_lowercase()
            .as_str()
        {
            "png" => "image/png",
            "jpg" | "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "tif" | "tiff" => "image/tiff",
            "svg" => "image/svg+xml",
            _ => return String::new(),
        };
        let extent = n
            .descendants()
            .find(|n| n.is_element() && n.tag_name().name() == "extent");
        let css = extent
            .map(|e| {
                format!(
                    "width:{}pt;height:{}pt",
                    num(attr(e, "cx"), 0.0) / 12700.0,
                    num(attr(e, "cy"), 0.0) / 12700.0
                )
            })
            .unwrap_or_default();
        format!(
            "<img alt=\"\" src=\"data:{mime};base64,{}\"{}>",
            base64::engine::general_purpose::STANDARD.encode(data),
            css_attr(&css)
        )
    }
    fn table(&mut self, n: Node<'_, '_>, part: &str) -> String {
        let properties = child(n, "tblPr");
        let style_id = properties.map(|p| val(p, "tblStyle")).unwrap_or("");
        let inherited = self
            .styles
            .as_ref()
            .and_then(|d| {
                d.descendants().find(|n| {
                    n.is_element()
                        && n.tag_name().name() == "style"
                        && attr(*n, "styleId") == style_id
                })
            })
            .and_then(|n| child(n, "tblPr"));
        let mut css = inherited.map(table_css).unwrap_or_default();
        css.push_str(&properties.map(table_css).unwrap_or_default());
        let grid_css = properties
            .and_then(|p| child(p, "tblBorders"))
            .or_else(|| inherited.and_then(|p| child(p, "tblBorders")))
            .and_then(|b| child(b, "insideH").or_else(|| child(b, "insideV")))
            .filter(|b| !matches!(attr(*b, "val"), "none" | "nil"))
            .map(|b| {
                format!(
                    "border:{}pt solid #{};",
                    (num(attr(b, "sz"), 4.0) / 8.0).max(0.5),
                    if hex(attr(b, "color")) {
                        attr(b, "color")
                    } else {
                        "000000"
                    }
                )
            })
            .unwrap_or_default();
        let rows: Vec<_> = n
            .children()
            .filter(|n| n.is_element() && n.tag_name().name() == "tr")
            .collect();
        let mut out = format!("<table{}><tbody>", css_attr(&css));
        for (ri, row) in rows.iter().enumerate() {
            out.push_str("<tr>");
            let mut grid_column = 0;
            for c in row
                .children()
                .filter(|n| n.is_element() && n.tag_name().name() == "tc")
            {
                let p = child(c, "tcPr");
                let colspan = p
                    .map(|p| num(val(p, "gridSpan"), 1.0) as usize)
                    .unwrap_or(1)
                    .max(1);
                let merge = p.and_then(|p| child(p, "vMerge"));
                if merge.is_some_and(|m| attr(m, "val") != "restart") {
                    grid_column += colspan;
                    continue;
                }
                let mut rowspan = 1;
                if merge.is_some() {
                    for next in rows.iter().skip(ri + 1) {
                        let mut col = 0;
                        let mut continues = false;
                        for cell in next
                            .children()
                            .filter(|n| n.is_element() && n.tag_name().name() == "tc")
                        {
                            let pr = child(cell, "tcPr");
                            if col == grid_column {
                                continues = pr
                                    .and_then(|p| child(p, "vMerge"))
                                    .is_some_and(|m| attr(m, "val") != "restart");
                                break;
                            }
                            col += pr
                                .map(|p| num(val(p, "gridSpan"), 1.0) as usize)
                                .unwrap_or(1)
                                .max(1);
                        }
                        if !continues {
                            break;
                        }
                        rowspan += 1;
                    }
                }
                let css = grid_css.clone() + &p.map(table_css).unwrap_or_default();
                out.push_str(&format!(
                    "<td colspan=\"{colspan}\" rowspan=\"{rowspan}\"{}>{}</td>",
                    css_attr(&css),
                    self.blocks(c, part)
                ));
                grid_column += colspan;
            }
            out.push_str("</tr>");
        }
        out.push_str("</tbody></table>");
        out
    }
    fn marker(&mut self, p: Node<'_, '_>) -> String {
        let Some(n) = child(p, "numPr") else {
            return String::new();
        };
        let id = val(n, "numId");
        if id == "0" {
            return String::new();
        }
        let level = (num(val(n, "ilvl"), 0.0) as usize).min(8);
        let mut marker = "•".to_owned();
        if let Some(doc) = &self.numbering {
            let abstract_id = doc
                .descendants()
                .find(|n| n.is_element() && n.tag_name().name() == "num" && attr(*n, "numId") == id)
                .map(|n| val(n, "abstractNumId"))
                .unwrap_or("");
            if let Some(abs) = doc.descendants().find(|n| {
                n.is_element()
                    && n.tag_name().name() == "abstractNum"
                    && attr(*n, "abstractNumId") == abstract_id
            }) {
                let levels: Vec<_> = abs
                    .children()
                    .filter(Node::is_element)
                    .filter(|n| n.tag_name().name() == "lvl")
                    .collect();
                if let Some(current) = levels
                    .iter()
                    .find(|n| num(attr(**n, "ilvl"), 0.0) as usize == level)
                {
                    marker = val(*current, "lvlText").into();
                    if val(*current, "numFmt") == "bullet"
                        && (marker.is_empty()
                            || marker
                                .chars()
                                .any(|c| ('\u{e000}'..='\u{f8ff}').contains(&c)))
                    {
                        marker = "•".into();
                    }
                    if val(*current, "numFmt") != "bullet" {
                        let counters = self.counters.entry(id.into()).or_insert([0; 9]);
                        counters[level] = if counters[level] == 0 {
                            num(val(*current, "start"), 1.0) as u32
                        } else {
                            counters[level] + 1
                        };
                        counters.iter_mut().skip(level + 1).for_each(|n| *n = 0);
                        for (i, counter) in counters.iter().enumerate().take(level + 1) {
                            let definition = levels
                                .iter()
                                .find(|n| num(attr(**n, "ilvl"), 0.0) as usize == i)
                                .unwrap_or(current);
                            marker = marker.replace(
                                &format!("%{}", i + 1),
                                &format_number((*counter).max(1), val(*definition, "numFmt")),
                            );
                        }
                    }
                }
            }
        }
        format!("<span class=\"marker\">{}</span>", xml(marker))
    }
    fn paragraph_marker(&mut self, prop: Option<Node<'_, '_>>, id: &str) -> String {
        if let Some(p) = prop
            && child(p, "numPr").is_some()
        {
            return self.marker(p);
        }
        let Some(text) = text_part(self.parts, "word/styles.xml") else {
            return String::new();
        };
        let Ok(styles) = Document::parse(text) else {
            return String::new();
        };
        let mut current = id;
        let mut seen = HashSet::new();
        while !current.is_empty() && seen.insert(current.to_owned()) {
            let Some(style) = styles.descendants().find(|n| {
                n.is_element() && n.tag_name().name() == "style" && attr(*n, "styleId") == current
            }) else {
                break;
            };
            if let Some(p) = child(style, "pPr")
                && child(p, "numPr").is_some()
            {
                return self.marker(p);
            }
            current = val(style, "basedOn");
        }
        String::new()
    }
}
fn format_number(mut n: u32, format: &str) -> String {
    if matches!(format, "lowerLetter" | "upperLetter") {
        let mut s = String::new();
        while n > 0 {
            n -= 1;
            s.insert(0, (b'a' + (n % 26) as u8) as char);
            n /= 26;
        }
        return if format == "upperLetter" {
            s.to_uppercase()
        } else {
            s
        };
    }
    if matches!(format, "lowerRoman" | "upperRoman") {
        n = n.min(3999);
        let mut s = String::new();
        for (v, t) in [
            (1000, "M"),
            (900, "CM"),
            (500, "D"),
            (400, "CD"),
            (100, "C"),
            (90, "XC"),
            (50, "L"),
            (40, "XL"),
            (10, "X"),
            (9, "IX"),
            (5, "V"),
            (4, "IV"),
            (1, "I"),
        ] {
            while n >= v {
                s.push_str(t);
                n -= v;
            }
        }
        return if format == "lowerRoman" {
            s.to_lowercase()
        } else {
            s
        };
    }
    n.to_string()
}
fn paragraph_css(p: Node<'_, '_>) -> String {
    let mut s = String::new();
    let align = match val(p, "jc") {
        "both" | "distribute" => "justify",
        a => a,
    };
    if ["left", "right", "center", "justify"].contains(&align) {
        s += &format!("text-align:{align};");
    }
    if let Some(n) = child(p, "spacing") {
        for (a, c) in [("before", "margin-top"), ("after", "margin-bottom")] {
            if !attr(n, a).is_empty() {
                s += &format!("{c}:{}pt;", num(attr(n, a), 0.0) / 20.0);
            }
        }
        if !attr(n, "line").is_empty() {
            if matches!(attr(n, "lineRule"), "" | "auto") {
                s += &format!("line-height:{};", num(attr(n, "line"), 240.0) / 240.0);
            } else {
                s += &format!("line-height:{}pt;", num(attr(n, "line"), 240.0) / 20.0);
            }
        }
    }
    if let Some(n) = child(p, "ind") {
        for (a, c) in [
            ("left", "margin-left"),
            ("right", "margin-right"),
            ("firstLine", "text-indent"),
        ] {
            if !attr(n, a).is_empty() {
                s += &format!("{c}:{}pt;", num(attr(n, a), 0.0) / 20.0);
            }
        }
        if !attr(n, "hanging").is_empty() {
            s += &format!("text-indent:-{}pt;", num(attr(n, "hanging"), 0.0) / 20.0);
        }
    }
    if on(p, "pageBreakBefore") {
        s += "break-before:page;";
    }
    if on(p, "keepNext") {
        s += "break-after:avoid-page;";
    }
    if on(p, "keepLines") {
        s += "break-inside:avoid;";
    }
    s += &shading(p);
    s
}
fn run_css(p: Node<'_, '_>) -> String {
    let mut s = String::new();
    for (n, c) in [
        ("b", "font-weight:bold;"),
        ("i", "font-style:italic;"),
        ("strike", "text-decoration:line-through;"),
        ("caps", "text-transform:uppercase;"),
        ("smallCaps", "font-variant:small-caps;"),
    ] {
        if on(p, n) {
            s += c;
        }
    }
    if child(p, "u").is_some() && val(p, "u") != "none" {
        s += "text-decoration:underline;";
    }
    if hex(val(p, "color")) {
        s += &format!("color:#{};", val(p, "color"));
    }
    let highlight = val(p, "highlight");
    if [
        "yellow",
        "green",
        "cyan",
        "magenta",
        "blue",
        "red",
        "black",
        "white",
        "darkBlue",
        "darkRed",
        "darkGreen",
        "lightGray",
    ]
    .contains(&highlight)
    {
        s += &format!("background-color:{highlight};");
    }
    if !val(p, "sz").is_empty() {
        s += &format!("font-size:{}pt;", num(val(p, "sz"), 22.0) / 2.0);
    }
    if let Some(f) = child(p, "rFonts") {
        let f = font(if attr(f, "ascii").is_empty() {
            attr(f, "hAnsi")
        } else {
            attr(f, "ascii")
        });
        if !f.is_empty() {
            s += &format!("font-family:'{f}';");
        }
    }
    match val(p, "vertAlign") {
        "superscript" => s += "vertical-align:super;font-size:.75em;",
        "subscript" => s += "vertical-align:sub;font-size:.75em;",
        _ => {}
    }
    s += &shading(p);
    s
}
fn shading(p: Node<'_, '_>) -> String {
    child(p, "shd")
        .filter(|n| hex(attr(*n, "fill")))
        .map(|n| format!("background-color:#{};", attr(n, "fill")))
        .unwrap_or_default()
}
fn table_css(p: Node<'_, '_>) -> String {
    let mut s = shading(p);
    if let Some(w) = child(p, "tblW").or_else(|| child(p, "tcW")) {
        match attr(w, "type") {
            "pct" => s += &format!("width:{}%;", num(attr(w, "w"), 5000.0) / 50.0),
            "dxa" => s += &format!("width:{}pt;", num(attr(w, "w"), 0.0) / 20.0),
            _ => {}
        }
    }
    match val(p, "vAlign") {
        "center" => s += "vertical-align:middle;",
        "bottom" => s += "vertical-align:bottom;",
        _ => {}
    }
    match val(p, "jc") {
        "center" => s += "margin-left:auto;margin-right:auto;",
        "right" => s += "margin-left:auto;margin-right:0;",
        _ => {}
    }
    if let Some(b) = child(p, "tblBorders").or_else(|| child(p, "tcBorders")) {
        for border in b.children().filter(Node::is_element) {
            if matches!(attr(border, "val"), "none" | "nil") {
                continue;
            }
            let side = match border.tag_name().name() {
                "top" => "border-top",
                "left" => "border-left",
                "right" => "border-right",
                "bottom" => "border-bottom",
                _ => "border",
            };
            let color = if hex(attr(border, "color")) {
                attr(border, "color")
            } else {
                "000000"
            };
            s += &format!(
                "{side}:{}pt solid #{color};",
                (num(attr(border, "sz"), 4.0) / 8.0).max(0.5)
            );
        }
    }
    if let Some(m) = child(p, "tcMar") {
        for side in ["top", "left", "right", "bottom"] {
            if let Some(n) = child(m, side) {
                s += &format!("padding-{side}:{}pt;", num(attr(n, "w"), 0.0) / 20.0);
            }
        }
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn renders_text_styles_and_escapes_html() {
        let parts=HashMap::from([("word/document.xml".into(),br#"<w:document xmlns:w="urn:w"><w:body><w:p><w:r><w:rPr><w:b/></w:rPr><w:t>A &amp; B &lt;script&gt;</w:t></w:r></w:p></w:body></w:document>"#.to_vec())]);
        let html = render_parts(&parts, "Test").unwrap();
        assert!(html.contains("font-weight:bold"));
        assert!(html.contains("A &amp; B &lt;script&gt;"));
        assert!(!html.contains("<script>"));
    }
    #[test]
    fn numbering_formats() {
        assert_eq!(format_number(27, "upperLetter"), "AA");
        assert_eq!(format_number(14, "lowerRoman"), "xiv");
    }
    #[test]
    fn inherits_list_and_table_styles() {
        let parts=HashMap::from([
            ("word/document.xml".into(),r#"<w:document xmlns:w="urn:w"><w:body><w:p><w:pPr><w:pStyle w:val="Bullet"/></w:pPr><w:r><w:t>Step</w:t></w:r></w:p><w:tbl><w:tblPr><w:tblStyle w:val="Grid"/></w:tblPr><w:tr><w:tc><w:p><w:r><w:t>Cell</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"#.as_bytes().to_vec()),
            ("word/styles.xml".into(),r#"<w:styles xmlns:w="urn:w"><w:style w:styleId="Bullet"><w:pPr><w:numPr><w:numId w:val="1"/></w:numPr></w:pPr></w:style><w:style w:styleId="Grid"><w:tblPr><w:tblBorders><w:insideH w:val="single" w:sz="4"/></w:tblBorders></w:tblPr></w:style></w:styles>"#.as_bytes().to_vec()),
            ("word/numbering.xml".into(),format!(r#"<w:numbering xmlns:w="urn:w"><w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="{}"/></w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>"#,'\u{f0b7}').into_bytes()),
        ]);
        let html = render_parts(&parts, "Styles").unwrap();
        assert!(html.contains("<span class=\"marker\">•</span>"));
        assert!(
            html.contains("<td colspan=\"1\" rowspan=\"1\" style=\"border:0.5pt solid #000000;")
        );
    }
}
