//! Look up a public cover URL and say which source found it.
//! cargo run --example cover -- "Panchiko" "Untitled Acoustic Song - Live" "LIVE" [duration_s] [local.jpg]
//!
//! With a local JPEG, picks the catalogue cover that looks most like it and
//! prints the distance (or "none" when nothing is close).

use overtone_core::{artwork, Track};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() < 2 {
        eprintln!("usage: cover <artist> <title> [album] [duration_s] [local.jpg]");
        std::process::exit(2);
    }
    let track = Track {
        artist: args[0].clone(),
        title: args[1].clone(),
        album: args.get(2).cloned().unwrap_or_default(),
        duration_ms: args.get(3).and_then(|d| d.parse::<f64>().ok()).map(|s| (s * 1000.0) as u64),
    };
    if let Some(path) = args.get(4) {
        let bytes = std::fs::read(path).expect("read local cover");
        let (rgba, w, h) = artwork::decode_jpeg(&bytes).expect("decode local cover");
        match artwork::cover_like(&track, &rgba, w as u32, h as u32) {
            Ok(Some(m)) => println!("{:?}  {:.3}  {}", m.source, m.distance, m.url),
            Ok(None) => println!("none close"),
            Err(e) => println!("error: {e}"),
        }
        return;
    }
    match artwork::cover(&track) {
        Ok(Some((url, source))) => println!("{source:?}  {url}"),
        Ok(None) => println!("none"),
        Err(e) => println!("error: {e}"),
    }
}
