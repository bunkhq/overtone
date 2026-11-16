//! Look up a public cover URL and say which source found it.
//! cargo run --example cover -- "Panchiko" "Untitled Acoustic Song - Live" "LIVE" [duration_s]

use overtone_core::{artwork, Track};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() < 2 {
        eprintln!("usage: cover <artist> <title> [album] [duration_s]");
        std::process::exit(2);
    }
    let track = Track {
        artist: args[0].clone(),
        title: args[1].clone(),
        album: args.get(2).cloned().unwrap_or_default(),
        duration_ms: args.get(3).and_then(|d| d.parse::<f64>().ok()).map(|s| (s * 1000.0) as u64),
    };
    match artwork::cover(&track) {
        Ok(Some((url, source))) => println!("{source:?}  {url}"),
        Ok(None) => println!("none"),
        Err(e) => println!("error: {e}"),
    }
}
