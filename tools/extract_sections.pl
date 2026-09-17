use strict; use warnings;
# Pulls structured course section records out of a decoded PDF content stream.
# Usage: perl extract_sections.pl <label> < file.raw
my $label = shift // "unknown";
local $/;
my $data = <>;

my $n = qr/-?[\d.]+/;
my (@items, $x, $y);
$x = 0; $y = 0;

while ($data =~ /
      (BT)\b
    | ($n)\s+($n)\s+($n)\s+($n)\s+($n)\s+($n)\s+Tm\b
    | ($n)\s+($n)\s+(?:Td|TD)\b
    | \[([^\[\]]*)\]\s*TJ
    /gsx) {
    if (defined $1) { $x = 0; $y = 0; next; }
    if (defined $2) { $x = $6 + 0; $y = $7 + 0; next; }
    if (defined $8) { $x += $8 + 0; $y += $9 + 0; next; }
    my $arr = $10; my $t = "";
    while ($arr =~ /<([0-9A-Fa-f]+)>/gs) {
        my $hex = $1;
        while ($hex =~ /([0-9A-Fa-f]{4})/g) {
            my $c = hex($1);
            $t .= ($c >= 32 && $c < 127) ? chr($c) : " ";
        }
    }
    next unless $t =~ /\S/;
    push @items, [ $x, $y, $t ];
}
exit 0 unless @items;

# Find the column gutter: the emptiest x band in the middle of the page.
my ($minx, $maxx) = ($items[0][0], $items[0][0]);
for my $i (@items) { $minx = $i->[0] if $i->[0] < $minx; $maxx = $i->[0] if $i->[0] > $maxx; }
my %dens;
$dens{ int($_->[0] / 20) }++ for @items;
my ($gutter, $best) = (($minx + $maxx) / 2, 1e9);
for my $b (int(($minx + ($maxx-$minx)*0.35)/20) .. int(($minx + ($maxx-$minx)*0.65)/20)) {
    my $d = $dens{$b} // 0;
    if ($d < $best) { $best = $d; $gutter = $b * 20 + 10; }
}

# Build rows per column, then read left column fully, then right.
my @lines;
for my $col (0, 1) {
    my %rows;
    for my $it (@items) {
        my $inleft = $it->[0] < $gutter ? 0 : 1;
        next unless $inleft == $col;
        push @{ $rows{ int($it->[1] / 4 + 0.5) } }, $it;
    }
    for my $k (sort { $b <=> $a } keys %rows) {
        my @c = sort { $a->[0] <=> $b->[0] } @{ $rows{$k} };
        my $line = ""; my $prev;
        for my $cell (@c) {
            $line .= "  " if defined $prev and $cell->[0] - $prev > 6;
            $line .= $cell->[2];
            $prev = $cell->[0] + length($cell->[2]) * 3;
        }
        $line =~ s/\s+/ /g; $line =~ s/^ | $//g;
        push @lines, [ $c[0][0], $line ] if $line =~ /\S/;
    }
}

# Walk the lines, tracking the most recent course code, and emit each section.
my $DAYS = qr/MTWTHF|MTWTH|TWTH|MWF|TTH|MW|WF|MF|TH|M|T|W|F/;
my $course = "";
my $title  = "";

sub csv { my @f = @_; return join(",", map { my $v = $_ // ""; $v =~ s/"/""/g; qq("$v") } @f); }

my $margin = 1e9;
for my $l (@lines) { $margin = $l->[0] if $l->[0] < $margin; }

for my $rec (@lines) {
    my ($lx, $line) = @$rec;
    # A course header sits at the column left margin, as "CMS 342K  TITLE".
    # Codes mentioned inside description text are indented, so they are skipped.
    if ($lx <= $margin + 8 and $line =~ /^\s*([A-Z]{2,4})\s+(\d{3}[A-Z]?)\b\s*(.*)$/) {
        my ($d, $num, $rest) = ($1, $2, $3);
        unless ($d =~ /^($DAYS)$/) {
            $course = "$d $num";
            $rest =~ s/^[^A-Za-z]+//;
            $title = (length($rest) > 3 && $rest =~ /^[A-Z0-9 \/&.,'-]{4,}$/) ? $rest : "";
        }
    }

    # Clean up the scanner artifacts that break matching.
    my $clean = $line;
    $clean =~ s/_/ /g;          # stray underscores glued to times and rooms
    $clean =~ s/\bl(?=TH\b)/T/g;
    $clean =~ s/\b1TH\b/TTH/g;  # "1TH" is a misread "TTH"
    $clean =~ s/\bMWE\b/MWF/g;  # "MWE" is a misread "MWF"
    $clean =~ s/\s+/ /g;

    # Section lines: unique number, days, time range, room. The instructor is
    # picked up afterwards on a best-effort basis so a garbled name cannot
    # stop the rest of the record from being read.
    while ($clean =~ /
        (\d{5})\s+
        ($DAYS)\s+
        (\d{3,4})\s*-\s*(\d{3,4})\s*(P?)\s+
        ([A-Z]{2,4})\s?(\d{1,2}\.\d{2,3}[A-Z]?|[A-Z]?\d{2,4}[A-Z]?(?:-[A-Z])?)
        /gx) {

        my ($uniq, $days, $st, $en, $pm, $bldg, $room) =
            ($1, $2, $3, $4, $5, $6, $7);

        my $tail = substr($clean, pos($clean));
        my ($instr) = $tail =~ /^\s*([A-Z][A-Za-z'\-]{1,}(?:[ ,]+[A-Z][A-Za-z'\-]*)*)/;
        $instr //= "";
        $instr =~ s/\s+$//; $instr =~ s/[.,]$//;
        $instr = "" if length($instr) < 3;
        # A run of ordinary words is description text, not a name.
        $instr = "" if $instr =~ /\b(the|and|for|with|course|meets|topics)\b/i;
        # Drop a department code that bled in from the next column.
        $instr =~ s/\s+[A-Z]{2,4}$// if $instr =~ /\s/ and $instr !~ /,/;

        # Sanity check the times. OCR turns 1230 into 4230 and 1130 into 4130.
        my $bad = "";
        for my $t ($st, $en) {
            my $h = length($t) >= 3 ? substr($t, 0, length($t) - 2) : 0;
            $bad = "suspect_time" if $h < 1 or $h > 12;
        }

        print csv($label, $course, $title, $uniq, $days, $st, $en . $pm, $bldg, $room, $instr, $bad), "\n";
    }
}
