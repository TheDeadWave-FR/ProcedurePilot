use crate::{storage, tags};
use anyhow::{Result, bail};
use std::{
    collections::{HashMap, HashSet},
    path::Path,
};

#[derive(Clone, Debug, PartialEq)]
pub struct ProcedureList {
    pub id: String,
    pub name: String,
    pub created: String,
    pub modified: String,
    pub procedures: Vec<String>,
}
impl ProcedureList {
    pub fn new() -> Self {
        Self {
            id: uuid::Uuid::new_v4().simple().to_string(),
            name: String::new(),
            created: storage::now(),
            modified: storage::now(),
            procedures: vec![],
        }
    }
}
impl Default for ProcedureList {
    fn default() -> Self {
        Self::new()
    }
}
pub fn load(path: &Path) -> Result<Vec<ProcedureList>> {
    if !path.try_exists()? {
        return Ok(vec![]);
    }
    let text = storage::read(path)?;
    let doc = roxmltree::Document::parse(&text)?;
    if !doc.root_element().has_tag_name("ProcedureLists") {
        bail!("Lists.xml invalide / Invalid Lists.xml");
    }
    let mut result = vec![];
    for n in doc
        .root_element()
        .children()
        .filter(|n| n.has_tag_name("List"))
    {
        let mut seen = HashSet::new();
        result.push(ProcedureList {
            id: n.attribute("id").unwrap_or("").into(),
            name: n.attribute("name").unwrap_or("").trim().into(),
            created: n.attribute("createdAtUtc").unwrap_or("").into(),
            modified: n.attribute("modifiedAtUtc").unwrap_or("").into(),
            procedures: n
                .children()
                .filter(|p| p.has_tag_name("Procedure"))
                .filter_map(|p| p.attribute("name"))
                .map(str::trim)
                .filter(|s| !s.is_empty() && seen.insert(s.to_lowercase()))
                .map(str::to_owned)
                .collect(),
        });
    }
    result.sort_by_key(|l| l.name.to_lowercase());
    Ok(result)
}
pub fn save(path: &Path, lists: &[ProcedureList]) -> Result<()> {
    let mut seen = HashSet::new();
    for list in lists {
        if list.name.trim().is_empty() || list.procedures.is_empty() {
            bail!("Nom et procédures requis / Name and procedures required");
        }
        if !seen.insert(list.name.trim().to_lowercase()) {
            bail!(
                "Nom de liste déjà utilisé / List name already exists: {}",
                list.name
            );
        }
    }
    let mut xml = format!(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<ProcedureLists version=\"1\" updatedAtUtc=\"{}\" count=\"{}\">\n",
        storage::now(),
        lists.len()
    );
    for l in lists {
        xml.push_str(&format!("  <List id=\"{}\" name=\"{}\" createdAtUtc=\"{}\" modifiedAtUtc=\"{}\" count=\"{}\">\n", storage::xml(&l.id), storage::xml(l.name.trim()), storage::xml(&l.created), storage::xml(&l.modified), l.procedures.len()));
        for (i, name) in l.procedures.iter().enumerate() {
            xml.push_str(&format!(
                "    <Procedure order=\"{}\" name=\"{}\"{} />\n",
                i + 1,
                storage::xml(name),
                code_attr(name)
            ));
        }
        xml.push_str("  </List>\n");
    }
    xml.push_str("</ProcedureLists>\n");
    storage::atomic_write(path, xml.as_bytes())
}
pub fn code_attr(name: &str) -> String {
    tags::code(name)
        .map(|c| format!(" code=\"{c}\""))
        .unwrap_or_default()
}
pub fn synchronize(
    lists: &mut Vec<ProcedureList>,
    names: &[String],
    renames: &HashMap<String, String>,
) -> Result<bool> {
    tags::unique(names.iter().map(String::as_str))?;
    let before = lists.clone();
    for l in lists.iter_mut() {
        let mut resolved = vec![];
        for stored in &l.procedures {
            let name = renames.get(&stored.to_lowercase()).unwrap_or(stored);
            let current = names
                .iter()
                .find(|n| n.eq_ignore_ascii_case(name))
                .or_else(|| {
                    tags::code(name)
                        .and_then(|c| names.iter().find(|n| tags::code(n).as_ref() == Some(&c)))
                });
            if let Some(n) = current
                && !resolved.contains(n)
            {
                resolved.push(n.clone());
            }
        }
        if resolved != l.procedures {
            l.procedures = resolved;
            l.modified = storage::now();
        }
    }
    lists.retain(|l| !l.procedures.is_empty());
    Ok(*lists != before)
}
/// Caller holds the workspace lease. Compare only the edited list, so unrelated edits can coexist.
pub fn commit(
    path: &Path,
    original: Option<&ProcedureList>,
    replacement: Option<ProcedureList>,
    names: &[String],
) -> Result<()> {
    let mut current = load(path)?;
    if let Some(old) = original {
        if current.iter().find(|l| l.id == old.id) != Some(old) {
            bail!(
                "Conflit : cette liste a été modifiée par un autre utilisateur. Rechargez-la. / Conflict: another user changed this list. Reload it."
            );
        }
        current.retain(|l| l.id != old.id);
    }
    if let Some(mut new) = replacement {
        if new.procedures.iter().any(|p| !names.contains(p)) {
            bail!("La bibliothèque a changé / Library changed; reload the procedures");
        }
        new.modified = storage::now();
        current.push(new);
    }
    save(path, &current)
}
