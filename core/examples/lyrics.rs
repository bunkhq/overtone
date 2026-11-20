//! Look up lyrics for a track and print them with timestamps.
//! cargo run --example lyrics -- "The Buttertones" "Tequila Mockingbird" [album] [duration_s]

use overtone_core::lyrics::{Chain, LyricsProvider, LyricsResult};
use overtone_core::Track;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.len() < 2 {
        eprintln!("usage: lyrics <artist> <title> [album] [duration_s]");
        std::process::exit(2);
    }
    let track = Track {
        artist: args[0].clone(),
        title: args[1].clone(),
        album: args.get(2).cloned().unwrap_or_default(),
        duration_ms: args.get(3).and_then(|d| d.parse::<f64>().ok()).map(|s| (s * 1000.0) as u64),
    };
    match Chain::default().lookup(&track) {
        Ok(LyricsResult::Found(l)) => {
            println!("{} lines, synced: {}", l.lines.len(), l.synced);
            for line in l.lines.iter().take(12) {
                let t = line.start_ms;
                println!("[{:02}:{:02}.{:02}] {}", t / 60_000, t / 1000 % 60, t / 10 % 100, if line.text.is_empty() { "♪" } else { &line.text });
            }
        }
        Ok(other) => println!("{other:?}"),
        Err(e) => {
            eprintln!("{e}");
            std::process::exit(1);
        }
    }
}
