-- 0012 — notater på kundekort
alter table public.kundekort add column if not exists notater text;
