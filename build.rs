use image::{
    ExtendedColorType,
    codecs::ico::{IcoEncoder, IcoFrame},
    imageops::FilterType,
};
use std::{env, fs, path::PathBuf};

fn main() {
    println!("cargo:rerun-if-changed=assets/app-symbol.png");
    println!("cargo:rerun-if-changed=build.rs");
    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    let logo = image::open("assets/app-symbol.png").expect("Application logo must be readable");
    let window_icon = logo.resize_exact(256, 256, FilterType::Lanczos3);
    window_icon
        .save(out.join("app-icon.png"))
        .expect("Write window icon");
    let frames: Vec<_> = [16, 20, 24, 32, 40, 48, 64, 128, 256]
        .into_iter()
        .map(|size| {
            let rgba = logo
                .resize_exact(size, size, FilterType::Lanczos3)
                .to_rgba8();
            IcoFrame::as_png(rgba.as_raw(), size, size, ExtendedColorType::Rgba8)
                .expect("Encode icon resolution")
        })
        .collect();
    let icon_path = out.join("ProcedurePilot.ico");
    IcoEncoder::new(fs::File::create(&icon_path).expect("Create icon"))
        .encode_images(&frames)
        .expect("Encode Windows icon");
    if env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let rc = out.join("ProcedurePilot.rc");
        fs::write(
            &rc,
            format!(
                "1 ICON \"{}\"\n",
                icon_path.display().to_string().replace('\\', "/")
            ),
        )
        .expect("Write icon resource");
        embed_resource::compile_for(&rc, ["ProcedurePilot"], embed_resource::NONE)
            .manifest_required()
            .expect("Embed application icon in Windows executable");
    }
}
