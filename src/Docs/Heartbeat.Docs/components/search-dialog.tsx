'use client';
import { useDocsSearch } from 'fumadocs-core/search/client';
import { fetchClient } from 'fumadocs-core/search/client/fetch';
import {
  SearchDialog,
  SearchDialogClose,
  SearchDialogContent,
  SearchDialogFooter,
  SearchDialogHeader,
  SearchDialogIcon,
  SearchDialogInput,
  SearchDialogList,
  SearchDialogOverlay,
  type SharedProps,
  TagsList,
  TagsListItem,
} from 'fumadocs-ui/components/dialog/search';
import { useState } from 'react';

export interface SearchTag {
  name: string;
  value: string;
}

/**
 * The search dialog with a filter for each section. Fumadocs' default dialog renders its footer outside the popup,
 * where the filter sits behind the overlay, so the footer is placed inside the content here.
 */
export function SectionSearchDialog({ tags = [], ...props }: SharedProps & { tags?: SearchTag[] }) {
  const [tag, setTag] = useState<string>();
  const search = useDocsSearch({ client: fetchClient({ tag }) });

  return (
    <SearchDialog {...search} {...props}>
      <SearchDialogOverlay />
      <SearchDialogContent>
        <SearchDialogHeader>
          <SearchDialogIcon />
          <SearchDialogInput />
          <SearchDialogClose />
        </SearchDialogHeader>
        <SearchDialogList />
        <SearchDialogFooter>
          <TagsList tag={tag} onTagChange={setTag} allowClear>
            {tags.map((item) => <TagsListItem key={item.value} value={item.value}>{item.name}</TagsListItem>)}
          </TagsList>
        </SearchDialogFooter>
      </SearchDialogContent>
    </SearchDialog>
  );
}
