#!/usr/bin/env ruby
# frozen_string_literal: true

require "pathname"
require "set"
require "uri"

ROOT = Pathname.new(File.expand_path("..", __dir__))

def markdown_files
  return ARGV.map { |path| Pathname.new(path).expand_path } unless ARGV.empty?

  output = IO.popen(
    ["git", "ls-files", "--cached", "--others", "--exclude-standard", "--", "*.md"],
    chdir: ROOT.to_s,
    &:read
  )
  output.lines(chomp: true).reject(&:empty?).map { |path| ROOT.join(path) }
end

def github_anchors(path)
  counts = Hash.new(0)
  anchors = Set.new
  in_fence = false
  File.foreach(path) do |line|
    if line.match?(/^\s*(```|~~~)/)
      in_fence = !in_fence
      next
    end
    next if in_fence

    match = line.match(/^\s{0,3}\#{1,6}\s+(.+?)\s*#*\s*$/)
    next unless match

    heading = match[1]
      .gsub(/<[^>]+>/, "")
      .gsub(/!?\[([^\]]+)\]\([^)]*\)/, "\\1")
      .gsub(/[`*_~]/, "")
      .downcase
      .gsub(/[^\p{L}\p{N}\-_ ]/u, "")
      .strip
      .gsub(/\s+/, "-")
    next if heading.empty?

    suffix = counts[heading]
    counts[heading] += 1
    anchors.add(suffix.zero? ? heading : "#{heading}-#{suffix}")
  end
  anchors
end

def extract_targets(path)
  targets = []
  in_fence = false
  File.foreach(path).with_index(1) do |line, number|
    if line.match?(/^\s*(```|~~~)/)
      in_fence = !in_fence
      next
    end
    next if in_fence

    searchable = line.gsub(/`[^`]*`/, "")
    searchable.scan(/!?\[[^\]]*\]\(\s*(<[^>]+>|[^\s)]+)(?:\s+[^)]*)?\)/) do |match|
      targets << [number, match.first]
    end
    if (definition = searchable.match(/^\s*\[[^\]]+\]:\s*(<[^>]+>|\S+)/))
      targets << [number, definition[1]]
    end
    searchable.scan(/<(?:a|img)\b[^>]+(?:href|src)=["']([^"']+)["'][^>]*>/i) do |match|
      targets << [number, match.first]
    end
  end
  targets
end

def ignored_target?(target)
  target.empty? ||
    target.start_with?("http://", "https://", "mailto:", "tel:", "data:") ||
    target.include?("{{") ||
    target.include?("${")
end

failures = []
anchor_cache = {}
markdown_files.each do |source|
  next unless source.file?

  extract_targets(source).each do |line, raw_target|
    target = raw_target.delete_prefix("<").delete_suffix(">").strip
    next if ignored_target?(target)

    path_part, fragment = target.split("#", 2)
    path_part = path_part.to_s.split("?", 2).first.to_s
    begin
      decoded_path = URI::DEFAULT_PARSER.unescape(path_part)
      decoded_fragment = fragment.nil? ? nil : URI::DEFAULT_PARSER.unescape(fragment)
    rescue ArgumentError => error
      failures << "#{source.relative_path_from(ROOT)}:#{line}: invalid URL encoding (#{error.message}): #{target}"
      next
    end

    destination = if decoded_path.empty?
      source
    elsif decoded_path.start_with?("/")
      ROOT.join(decoded_path.delete_prefix("/"))
    else
      source.dirname.join(decoded_path).cleanpath
    end
    unless destination.exist?
      failures << "#{source.relative_path_from(ROOT)}:#{line}: missing target: #{target}"
      next
    end

    next if decoded_fragment.nil? || decoded_fragment.empty?
    next unless destination.file? && destination.extname.downcase == ".md"

    key = destination.expand_path.to_s
    anchors = anchor_cache[key] ||= github_anchors(destination)
    unless anchors.include?(decoded_fragment.downcase)
      failures << "#{source.relative_path_from(ROOT)}:#{line}: missing anchor ##{decoded_fragment} in #{destination.relative_path_from(ROOT)}"
    end
  end
end

if failures.empty?
  puts "Markdown links: pass"
  exit 0
end

warn failures.join("\n")
warn "Markdown links: #{failures.length} failure(s)"
exit 1
