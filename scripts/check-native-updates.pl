#!/usr/bin/env perl
use strict;
use warnings;
use JSON::PP;
use LWP::UserAgent;
use version;

# Configuration
my $REPO_ROOT = $ENV{GITHUB_WORKSPACE} || '/Users/jas88/Developer/Github/libarchive.net';
my $LINUX_SCRIPT = "$REPO_ROOT/native/build-linux.sh";
my $MACOS_SCRIPT = "$REPO_ROOT/native/build-macos.sh";
my $DRY_RUN = $ENV{DRY_RUN} // 1;

# User agent for HTTP requests
my $ua = LWP::UserAgent->new(
    agent => 'libarchive.net-dependency-checker/1.0',
    timeout => 30,
);

# Dependency definitions
my %DEPS = (
    'libarchive' => {
        current => '3.7.3',
        check_type => 'github_releases',
        repo => 'libarchive/libarchive',
        url_pattern_linux => 'https://github.com/libarchive/libarchive/releases/download/v%s/libarchive-%s.tar.xz',
        url_pattern_macos => 'https://github.com/libarchive/libarchive/releases/download/v%s/libarchive-%s.tar.xz',
    },
    'lz4' => {
        current => '1.9.4',
        check_type => 'github_releases',
        repo => 'lz4/lz4',
        url_pattern_linux => 'https://github.com/lz4/lz4/archive/refs/tags/v%s.tar.gz',
        url_pattern_macos => 'https://github.com/lz4/lz4/archive/refs/tags/v%s.tar.gz',
    },
    'zstd' => {
        current => '1.5.6',
        check_type => 'github_releases',
        repo => 'facebook/zstd',
        url_pattern_linux => 'https://github.com/facebook/zstd/releases/download/v%s/zstd-%s.tar.gz',
        url_pattern_macos => 'https://github.com/facebook/zstd/releases/download/v%s/zstd-%s.tar.gz',
    },
    'xz' => {
        current => '5.4.6',
        check_type => 'github_releases',
        repo => 'tukaani-project/xz',
        url_pattern_linux => 'https://github.com/tukaani-project/xz/releases/download/v%s/xz-%s.tar.xz',
        url_pattern_macos => 'https://github.com/tukaani-project/xz/releases/download/v%s/xz-%s.tar.xz',
    },
    'zlib' => {
        current => '1.3.1',
        check_type => 'web_scrape',
        check_url => 'https://zlib.net/',
        version_regex => qr/zlib-(\d+\.\d+\.\d+)\.tar\.xz/,
        url_pattern_linux => 'https://zlib.net/zlib-%s.tar.xz',
        url_pattern_macos => 'https://zlib.net/zlib-%s.tar.xz',
    },
    'libxml2' => {
        current => '2.12.6',
        check_type => 'gnome_release',
        major_minor => '2.12',
        check_url => 'https://download.gnome.org/sources/libxml2/2.12/',
        version_regex => qr/libxml2-(\d+\.\d+\.\d+)\.tar\.xz/,
        url_pattern_linux => 'https://download.gnome.org/sources/libxml2/%M/libxml2-%s.tar.xz',
        url_pattern_macos => 'https://download.gnome.org/sources/libxml2/%M/libxml2-%s.tar.xz',
    },
    'lzo' => {
        current => '2.10',
        check_type => 'web_scrape',
        check_url => 'https://www.oberhumer.com/opensource/lzo/download/',
        version_regex => qr/lzo-(\d+\.\d+)\.tar\.gz/,
        url_pattern_linux => 'https://www.oberhumer.com/opensource/lzo/download/lzo-%s.tar.gz',
        url_pattern_macos => 'https://www.oberhumer.com/opensource/lzo/download/lzo-%s.tar.gz',
    },
    'bzip2' => {
        current => '1.0.8',
        check_type => 'web_scrape',
        check_url => 'https://www.sourceware.org/pub/bzip2/',
        version_regex => qr/bzip2-(\d+\.\d+\.\d+)\.tar\.gz/,
        url_pattern_linux => 'https://www.sourceware.org/pub/bzip2/bzip2-latest.tar.gz',
        url_pattern_macos => 'https://www.sourceware.org/pub/bzip2/bzip2-latest.tar.gz',
        note => 'Uses "latest" tarball - version check informational only',
    },
);

sub check_github_releases {
    my ($repo) = @_;

    my $url = "https://api.github.com/repos/$repo/releases/latest";
    my $response = $ua->get($url, 'Accept' => 'application/vnd.github.v3+json');

    unless ($response->is_success) {
        warn "Failed to fetch GitHub releases for $repo: " . $response->status_line;
        return undef;
    }

    my $data = decode_json($response->content);
    my $tag = $data->{tag_name};

    # Remove 'v' prefix if present
    $tag =~ s/^v//;

    return $tag;
}

sub check_web_scrape {
    my ($url, $regex) = @_;

    my $response = $ua->get($url);
    unless ($response->is_success) {
        warn "Failed to fetch $url: " . $response->status_line;
        return undef;
    }

    my $content = $response->content;
    my @versions;

    while ($content =~ /$regex/g) {
        push @versions, $1;
    }

    # Return the highest version found
    return undef unless @versions;
    @versions = sort { version->parse($b) <=> version->parse($a) } @versions;
    return $versions[0];
}

sub check_gnome_release {
    my ($url, $regex, $major_minor) = @_;

    # First, check if there's a newer major.minor version
    my $base_url = 'https://download.gnome.org/sources/libxml2/';
    my $response = $ua->get($base_url);

    if ($response->is_success) {
        my @versions;
        while ($response->content =~ m{href="(\d+\.\d+)/"}g) {
            push @versions, $1;
        }
        @versions = sort { version->parse($b) <=> version->parse($a) } @versions;
        $major_minor = $versions[0] if @versions;
    }

    # Now check for the latest patch version
    $url = "https://download.gnome.org/sources/libxml2/$major_minor/";
    return check_web_scrape($url, $regex);
}

sub compare_versions {
    my ($current, $latest) = @_;

    eval {
        return version->parse($latest) > version->parse($current);
    };
    if ($@) {
        warn "Version comparison failed for $current vs $latest: $@";
        return 0;
    }
}

sub read_file {
    my ($path) = @_;
    open my $fh, '<', $path or die "Cannot read $path: $!";
    local $/;
    my $content = <$fh>;
    close $fh;
    return $content;
}

sub write_file {
    my ($path, $content) = @_;
    open my $fh, '>', $path or die "Cannot write $path: $!";
    print $fh $content;
    close $fh;
}

sub update_script {
    my ($script_path, $dep_name, $old_version, $new_version) = @_;

    my $content = read_file($script_path);
    my $dep = $DEPS{$dep_name};

    # Build old and new URL patterns
    my $script_type = $script_path =~ /linux/ ? 'linux' : 'macos';
    my $url_pattern_key = "url_pattern_$script_type";

    my $old_url = sprintf($dep->{$url_pattern_key}, ($old_version) x 2);
    my $new_url = sprintf($dep->{$url_pattern_key}, ($new_version) x 2);

    # Handle special case for %M (major.minor) replacement
    if ($new_url =~ /%M/) {
        my ($major_minor) = $new_version =~ /^(\d+\.\d+)/;
        $new_url =~ s/%M/$major_minor/g;
        $old_url =~ s/%M/\d+\.\d+/;  # Make it a regex for matching
    }

    # Replace the URL
    my $replaced = ($content =~ s/\Q$old_url\E/$new_url/g);

    if ($replaced) {
        write_file($script_path, $content) unless $DRY_RUN;
        return 1;
    }

    warn "Failed to update $dep_name in $script_path";
    return 0;
}

sub create_pr {
    my ($updates_ref) = @_;
    my @updates = @$updates_ref;

    return unless @updates;

    # Create branch name
    my $branch_name = 'dependabot/native/' . join('-', map { lc($_->{name}) } @updates);
    $branch_name =~ s/[^a-z0-9\-]/-/g;

    # Create commit message
    my $commit_title = scalar(@updates) == 1
        ? "Bump native $updates[0]{name} from $updates[0]{old} to $updates[0]{new}"
        : "Bump " . scalar(@updates) . " native dependencies";

    my $commit_body = join("\n", map {
        "- Bump $_->{name} from $_->{old} to $_->{new}"
    } @updates);

    my $pr_body = "## Native Dependency Updates\n\n$commit_body\n\n";
    $pr_body .= "### Changes\n";
    for my $update (@updates) {
        $pr_body .= "- **$update->{name}**: `$update->{old}` → `$update->{new}`\n";
        if (my $note = $DEPS{$update->{name}}{note}) {
            $pr_body .= "  - *Note: $note*\n";
        }
    }

    $pr_body .= "\n### Build Scripts Updated\n";
    $pr_body .= "- `native/build-linux.sh`\n";
    $pr_body .= "- `native/build-macos.sh`\n";
    $pr_body .= "\n### Testing\n";
    $pr_body .= "CI will rebuild native libraries and run tests on all platforms.\n";

    if ($DRY_RUN) {
        print "\n=== DRY RUN: Would create PR ===\n";
        print "Branch: $branch_name\n";
        print "Title: $commit_title\n";
        print "Body:\n$pr_body\n";
        return;
    }

    # Git operations
    system("git", "checkout", "-b", $branch_name) == 0 or die "Failed to create branch";
    system("git", "add", $LINUX_SCRIPT, $MACOS_SCRIPT) == 0 or die "Failed to stage changes";
    system("git", "commit", "-m", "$commit_title\n\n$commit_body") == 0 or die "Failed to commit";
    system("git", "push", "-u", "origin", $branch_name) == 0 or die "Failed to push";

    # Create PR using gh CLI
    my $pr_file = "/tmp/pr-body-$$.md";
    write_file($pr_file, $pr_body);
    system("gh", "pr", "create", "-R", "jas88/libarchive.net",
           "--title", $commit_title,
           "--body-file", $pr_file,
           "--label", "dependencies",
           "--label", "native") == 0 or die "Failed to create PR";
    unlink $pr_file;
}

# Main execution
sub main {
    print "Checking native dependency versions...\n";

    my @updates;

    for my $dep_name (sort keys %DEPS) {
        my $dep = $DEPS{$dep_name};
        my $current = $dep->{current};
        my $latest;

        print "Checking $dep_name (current: $current)... ";

        if ($dep->{check_type} eq 'github_releases') {
            $latest = check_github_releases($dep->{repo});
        } elsif ($dep->{check_type} eq 'web_scrape') {
            $latest = check_web_scrape($dep->{check_url}, $dep->{version_regex});
        } elsif ($dep->{check_type} eq 'gnome_release') {
            $latest = check_gnome_release($dep->{check_url}, $dep->{version_regex}, $dep->{major_minor});
        }

        unless (defined $latest) {
            print "ERROR: Could not determine latest version\n";
            next;
        }

        print "latest: $latest";

        if (compare_versions($current, $latest)) {
            print " ⚠️  UPDATE AVAILABLE\n";
            push @updates, {
                name => $dep_name,
                old => $current,
                new => $latest,
            };

            # Update both scripts
            update_script($LINUX_SCRIPT, $dep_name, $current, $latest);
            update_script($MACOS_SCRIPT, $dep_name, $current, $latest);
        } else {
            print " ✓\n";
        }
    }

    if (@updates) {
        print "\n" . scalar(@updates) . " updates found!\n";
        create_pr(\@updates);
    } else {
        print "\nAll dependencies are up to date ✓\n";
    }
}

main();
