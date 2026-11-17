//! Word filter for text Overtone publishes (the Discord status and card).
//!
//! A matched word is replaced whole by asterisks, one per character, so the
//! line keeps its shape: "what the fuck" -> "what the ****". Two levels:
//! [`CensorLevel::Slurs`] removes slurs and hateful terms only;
//! [`CensorLevel::All`] removes those plus swear words.
//!
//! Matching is per word (runs of letters, digits and inner apostrophes), after
//! folding case and accents, simple leetspeak (`f4ck`, `sh1t`) and stretched
//! letters (`fuuuck`). Each term matches one of three ways, chosen so ordinary
//! words survive ("Scunthorpe", "class", "cocktail", "raccoon", "Dickens"):
//! - whole: the word itself (inflections are listed explicitly);
//! - prefix: the word starts with it (`bitch` -> `bitches`, `bitchy`);
//! - infix: the word contains it (only for stems no ordinary word contains).

use unicode_normalization::UnicodeNormalization;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[cfg_attr(feature = "uniffi", derive(uniffi::Enum))]
pub enum CensorLevel {
    Off,
    /// Slurs and hateful terms only.
    Slurs,
    /// Slurs plus swear words.
    #[default]
    All,
}

impl CensorLevel {
    /// 0 off, 1 slurs, 2 all (the C ABI's encoding).
    pub fn from_i32(v: i32) -> Self {
        match v {
            0 => Self::Off,
            1 => Self::Slurs,
            _ => Self::All,
        }
    }
}

enum Match {
    Whole,
    Prefix,
    Infix,
}

use Match::*;

const SLURS: &[(&str, Match)] = &[
    ("nigger", Prefix),
    ("nigga", Prefix),
    ("niggaz", Whole),
    ("faggot", Infix),
    ("fagot", Prefix),
    ("fag", Whole),
    ("fags", Whole),
    ("dyke", Whole),
    ("dykes", Whole),
    ("tranny", Whole),
    ("trannies", Whole),
    ("shemale", Prefix),
    ("retard", Whole),
    ("retards", Whole),
    ("retarded", Whole),
    ("chink", Whole),
    ("chinks", Whole),
    ("spic", Whole),
    ("spics", Whole),
    ("spick", Whole),
    ("kike", Whole),
    ("kikes", Whole),
    ("wetback", Prefix),
    ("gook", Whole),
    ("gooks", Whole),
    ("raghead", Prefix),
    ("towelhead", Prefix),
    ("beaner", Prefix),
    ("paki", Whole),
    ("pakis", Whole),
    ("coon", Whole),
    ("coons", Whole),
    ("jigaboo", Prefix),
    ("porchmonkey", Prefix),
    ("sambo", Whole),
    ("wop", Whole),
    ("wops", Whole),
    ("dago", Whole),
    ("dagos", Whole),
    ("gyppo", Prefix),
];

const SWEARS: &[(&str, Match)] = &[
    ("fuck", Infix),
    ("shit", Infix),
    ("cunt", Prefix),
    ("bitch", Prefix),
    ("asshol", Prefix),
    ("arsehol", Prefix),
    ("bastard", Prefix),
    ("wank", Prefix),
    ("twat", Prefix),
    ("slut", Prefix),
    ("whore", Prefix),
    ("piss", Whole),
    ("pissed", Whole),
    ("pissing", Whole),
    ("pisses", Whole),
    ("pissy", Whole),
    ("pisshead", Prefix),
    ("bollock", Prefix),
    ("dickhead", Prefix),
    ("cocksuck", Prefix),
    ("douche", Prefix),
    ("ass", Whole),
    ("asses", Whole),
    ("arse", Whole),
    ("arses", Whole),
    ("damn", Whole),
    ("damned", Whole),
    ("damnit", Whole),
    ("dammit", Whole),
    ("goddamn", Prefix),
    ("dick", Whole),
    ("dicks", Whole),
    ("cock", Whole),
    ("cocks", Whole),
    ("pussy", Whole),
    ("pussies", Whole),
    ("prick", Whole),
    ("pricks", Whole),
    ("crap", Whole),
    ("crappy", Whole),
    ("hoe", Whole),
    ("hoes", Whole),
    ("dumbass", Prefix),
    ("jackass", Prefix),
    ("badass", Prefix),
    ("smartass", Prefix),
    ("fatass", Prefix),
    ("kickass", Whole),
    ("bugger", Whole),
    ("wtf", Whole),
    ("stfu", Whole),
];

/// `text` with every matched word replaced by asterisks, one per character.
pub fn censor(text: &str, level: CensorLevel) -> String {
    if level == CensorLevel::Off {
        return text.to_string();
    }
    let mut out = String::with_capacity(text.len());
    let chars: Vec<char> = text.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        if !chars[i].is_alphanumeric() {
            out.push(chars[i]);
            i += 1;
            continue;
        }
        // A word: alphanumerics, plus apostrophes between them ("motherfuckin'" keeps its own).
        let start = i;
        while i < chars.len() && (chars[i].is_alphanumeric() || (is_apostrophe(chars[i]) && chars.get(i + 1).is_some_and(|c| c.is_alphanumeric()))) {
            i += 1;
        }
        let word: String = chars[start..i].iter().collect();
        if is_bad(&word, level) {
            out.extend(std::iter::repeat_n('*', i - start));
        } else {
            out.push_str(&word);
        }
    }
    out
}

fn is_apostrophe(c: char) -> bool {
    matches!(c, '\'' | '’' | '‘')
}

fn is_bad(word: &str, level: CensorLevel) -> bool {
    let norm = normalize(word);
    if norm.is_empty() {
        return false;
    }
    let variants = [collapse(&norm, 1), collapse(&norm, 2), norm];
    let lists: &[&[(&str, Match)]] = match level {
        CensorLevel::Off => return false,
        CensorLevel::Slurs => &[SLURS],
        CensorLevel::All => &[SLURS, SWEARS],
    };
    variants.iter().any(|w| {
        lists.iter().flat_map(|l| l.iter()).any(|(term, how)| match how {
            Whole => w == term,
            Prefix => w.starts_with(term),
            Infix => w.contains(term),
        })
    })
}

/// Lowercase, accents folded, apostrophes dropped, simple leetspeak undone.
fn normalize(word: &str) -> String {
    word.nfd()
        .filter(|c| c.is_alphanumeric())
        .flat_map(char::to_lowercase)
        .map(|c| match c {
            '0' => 'o',
            '1' => 'i',
            '3' => 'e',
            '4' => 'a',
            '5' => 's',
            '7' => 't',
            c => c,
        })
        .collect()
}

/// Runs of three or more of the same letter shortened to `keep` ("fuuuck" -> "fuck" with 1).
fn collapse(s: &str, keep: usize) -> String {
    let mut out = String::with_capacity(s.len());
    let chars: Vec<char> = s.chars().collect();
    let mut i = 0;
    while i < chars.len() {
        let mut j = i;
        while j < chars.len() && chars[j] == chars[i] {
            j += 1;
        }
        let run = j - i;
        out.extend(std::iter::repeat_n(chars[i], if run >= 3 { keep } else { run }));
        i = j;
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn replaces_whole_words_with_asterisks() {
        assert_eq!(censor("What the fuck is this shit", CensorLevel::All), "What the **** is this ****");
        assert_eq!(censor("Motherfucker!", CensorLevel::All), "************!");
        assert_eq!(censor("bullshit, dumbass", CensorLevel::All), "********, *******");
    }

    #[test]
    fn levels() {
        let line = "damn you, faggot";
        assert_eq!(censor(line, CensorLevel::Off), line);
        assert_eq!(censor(line, CensorLevel::Slurs), "damn you, ******");
        assert_eq!(censor(line, CensorLevel::All), "**** you, ******");
    }

    #[test]
    fn variants_are_caught() {
        assert_eq!(censor("SH1T", CensorLevel::All), "****");
        assert_eq!(censor("4ss", CensorLevel::All), "***");
        assert_eq!(censor("fuuuuck", CensorLevel::All), "*******");
        assert_eq!(censor("sh1tty", CensorLevel::All), "******");
        assert_eq!(censor("fückin'", CensorLevel::All), "******'");
        assert_eq!(censor("Bitches", CensorLevel::All), "*******");
    }

    #[test]
    fn ordinary_words_survive() {
        for text in [
            "Scunthorpe", "class", "assassin", "passion", "cocktail", "raccoon", "Dickens", "classic", "shiitake",
            "therapist", "Spice Girls", "hello", "grass", "cucumber", "assume", "Hancock", "peacock", "skyscraper",
            "niggle", "snigger", "fire retardant", "Paris", "documents", "Penistone", "hoedown", "pissarro",
        ] {
            assert_eq!(censor(text, CensorLevel::All), text, "{text}");
        }
    }

    #[test]
    fn keeps_punctuation_and_spacing() {
        assert_eq!(censor("  (shit) — “fuck”…  ", CensorLevel::All), "  (****) — “****”…  ");
        assert_eq!(censor("", CensorLevel::All), "");
    }
}
